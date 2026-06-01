# Rule Engine: SQLite + Trie 统一规则引擎

## 目标

将当前硬编码的规则类迁移为 SQLite 存储 + 内存预加载 + Trie 多模式匹配的统一规则引擎，解决 Issue #6。

## 范围

### 迁移的规则（现有类废弃）

**Trie 匹配型（关键词查找 + 条件判断）：**

| 现有类 | 规则类型 | 说明 |
|--------|----------|------|
| `GenderConflictRule` | `keyword_negation` | 关键词 + 否定检测 + 排除模式（术后等） |
| `CriticalSignRule` | `keyword_negation` | 危急征象关键词 + 否定检测 |
| `AgeConflictRule` | `keyword_age` | 关键词 + 年龄范围判断 |
| `DeviceConflictRule` | `keyword_device` | 检查设备匹配 + 禁止词检测 |
| `ScanEnhanceConflictRule` | `keyword_scan` | 检查方式条件 + 增强描述词检测 |
| `PhraseTypoRule` | `keyword_replace` | 错字→正确词映射 + SubType=phrase_typo |
| `TerminologyStandardRule` | `keyword_replace` | 非标准术语→标准术语映射 + SubType=terminology |
| `AnatomyTermRule` | `keyword_replace` | 非标准解剖名→标准解剖名映射 + SubType=anatomy_nonstandard |
| `ColloquialTermRule` | `keyword_replace` | 口语化词→建议映射 + SubType=colloquial |

**特殊逻辑型（内部有复杂判断，不走 Trie）：**

| 现有类 | 规则类型 | 说明 |
|--------|----------|------|
| `DirectionConflictRule` | `direction_compare` | 方位词 Findings vs Impression 比较 |
| `FindingsImpressionConsistencyRule` | `cross_section` | 所见阴性+结论阳性矛盾 / 程度矛盾 |
| `ComparisonDescriptionRule` | `cross_section` | 对比词出现→必须有比较结论 |
| `AdviceConsistencyRule` | `cross_section` | 可疑诊断→需随访 / 良性→不应过度检查 |
| `LesionCompletenessRule` | `cross_section` | 有病灶→必须有尺寸 / 多发→必须有数量 |
| `RadsClassificationRule` | `cross_section` | 报告类型→必须含对应 RADS 分级 |
| `DuplicateCharRule` | `regex_duplicate` | 正则叠字检测 + 白名单 |
| `SentencePunctuationRule` | `sentence_check` | 句末标点检查（分行+边界规则） |
| `PatientInfoRule` | `field_validation` | 请求字段校验（性别/年龄/部位/设备） |

**9 种规则类型 = 6 种 Trie 匹配型 + 3 种特殊逻辑型。** 全部存入 SQLite，在 RuleExecutor 中按类型分发到对应 handler。

### 保留不变

- `UnitFormatRule`（测量单位冲突检测）

## 架构

```
QcService (管线不变)
    │
    ├── UnitFormatRule (保留，不变)
    │
    └── RuleEngine (新，单例)
            ├── SqliteRuleStore     ── 启动时 LoadAll() → List<RuleDef>
            ├── TrieMatcher         ── Build(keywords) → O(n) 单遍匹配
            └── RuleExecutor        ── 按 rule_type 分发，生成 QcIssueDto[]
```

## SQLite Schema

```sql
-- rules.db，存放于 knowledge/ 目录
CREATE TABLE rule_def (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    rule_type   TEXT NOT NULL,
    name        TEXT NOT NULL,
    category    TEXT NOT NULL,
    severity    TEXT NOT NULL,
    params_json TEXT,
    description TEXT,
    is_active   INTEGER DEFAULT 1,
    sort_order  INTEGER DEFAULT 0
);

CREATE TABLE rule_keyword (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    rule_id     INTEGER NOT NULL REFERENCES rule_def(id),
    keyword     TEXT NOT NULL,
    keyword_len INTEGER NOT NULL,
    priority    INTEGER DEFAULT 0,
    is_exclude  INTEGER DEFAULT 0,
    extra_data  TEXT
);

CREATE INDEX idx_rule_id ON rule_keyword(rule_id);
CREATE INDEX idx_keyword_len ON rule_keyword(keyword_len DESC);
```

### params_json 原型（按规则类型）

```json
// keyword_device: 设备-禁止词映射
{"device_mapping": {"CT": ["MRI示","T1WI"], "MRI": ["CT示","CTA示"], ...}}

// keyword_scan: 触发条件
{"trigger_method": "平扫", "match_when": "contains"}

// keyword_replace: 替换类型标识
{"sub_type": "phrase_typo"}  // or "terminology", "anatomy_nonstandard", "colloquial"

// keyword_negation, keyword_age: null（参数在 rule_keyword.extra_data 中）

// cross_section: 条件链定义
{
  "positive_set": ["结节","肿块","占位"],     // 条件A关键词
  "negative_set": ["大小","直径","cm","mm"],  // 条件B关键词
  "match_mode": "if_A_then_B",                // if_A_then_B | if_A_not_B | severity_compare
  "search_section": "findings"                // findings | impression | full_text
}

// regex_duplicate: 正则参数
{"valid_duplicates": ["慢","常","渐",...]}

// sentence_check, field_validation: null（逻辑固定）
// direction_compare: null（逻辑固定）
```

### rule_keyword.extra_data 原型

```
// keyword_age: "0-20" (min_age-max_age)
// keyword_negation: null
// keyword_replace: "正确形式/建议文字" (replacement text)
// keyword_device, keyword_scan: null
```

## TrieMatcher

每个 `rule_keyword` 行对应一个模式串，构建为一棵 Trie：

```
           根
         /  |  \
       子  主  脑
      /    |     \
    宫   动脉夹层  出
   /      (id=5)    \
 肌瘤              血
(id=1)           (id=12)
```

- 叶结点存储 `List<rule_keyword_id>`，支持同一路径多个模式
- `FindAll(text)` 返回 `List<(keyword, startPos, ruleKeywordId)>`，单遍 O(n)
- 长词优先：构建时按 `keyword_len DESC` 顺序插入，短词不覆盖长词的叶结点标记

## RuleExecutor 分发逻辑

```
Execute(QcRequest):
  // ── 第一阶段：Trie 扫描全文本 ──
  fullText = findings + impression
  trieHits = TrieMatcher.FindAll(fullText)
  trieHitsById = trieHits.GroupBy(kwId → ruleId)

  // ── 第二阶段：逐规则执行 ──
  for each rule in rules (按 sort_order):
    switch rule.rule_type:

      // ── Trie 匹配型 ──
      keyword_negation:
        for hit in trieHitsById[rule.id]:
          if NegationDetector.IsNegated(fullText, hit.keyword) → skip
          if HasExcludePattern(fullText, hit.keyword) → skip
          → add issue(severity, description)

      keyword_age:
        if request.PatientAge == null → skip
        for hit in trieHitsById[rule.id]:
          (min, max) = parse(hit.extra_data)  // e.g. "0-20"
          if age in [min, max] → add issue

      keyword_device:
        if request.ExamDevice == null → skip
        device_group = params_json.device_mapping[matched_device]
        for banned_term in device_group:
          if TrieMatcher.Contains(banned_term) → add issue

      keyword_scan:
        if request.ExamMethod == null → skip
        if !ExamMethod.Contains(params_json.trigger_method) → skip
        for hit in trieHitsById[rule.id] → add issue

      keyword_replace:
        for hit in trieHitsById[rule.id]:
          → add issue(OriginalText=hit.keyword,
                      SuggestedText=hit.extra_data,  // 替换/建议文字
                      SubType=params_json.sub_type)

      // ── 非 Trie 特殊逻辑型 ──
      direction_compare:
        // 内部固定逻辑：同时出现左/右方位词时比较 Findings 和 Impression
        left/right keywords in findings vs impression → add issue

      cross_section:
        mode = params_json.match_mode
        if mode == "if_A_then_B":
          // 如 LesionCompletenessRule: 有病灶→须有尺寸
          // 如 ComparisonDescriptionRule: 有对比词→须有结论
          hasA = positive_set.Any(kw → section.Contains(kw))
          hasB = negative_set.Any(kw → section.Contains(kw))
          if hasA && !hasB → add issue

        if mode == "if_A_not_B":
          // 如 AdviceConsistencyRule: 可疑诊断→须有随访
          hasA = positive_set.Any(...)
          hasNotB = !negative_set.Any(...)
          if hasA && hasNotB → add issue

        if mode == "severity_compare":
          // 如 FindingsImpressionConsistencyRule: 轻度vs重度
          // positive_set = mild words, negative_set = severe words
          // findings 含 mild && impression 含 severe → issue

      regex_duplicate:
        for each regex match in fullText:
          if match.char NOT in valid_duplicates → add issue

      sentence_check:
        // 分行检查句末标点
        for each line in (findings + impression):
          if line is not a list item && last char not in valid_endings → issue

      field_validation:
        // 请求字段校验
        if gender not "男"/"女" → issue
        if age < 0 or > 150 → issue
        if exam_part or exam_device empty → issue
```

## NegationDetector（复用现有）

所有 `keyword_simple` 和 `keyword_negation` 规则匹配后先过 NegationDetector。排除模式（如"切除术后"、"术后复查"）作为 `rule_keyword` 中 `is_exclude=1` 的行，在 `keyword_negation` 类型中额外检查。

## 执行流程: QcService 集成

```csharp
// Program.cs 启动时
var ruleEngine = new RuleEngine("knowledge/rules.db");
builder.Services.AddSingleton(ruleEngine);

// QcService 中
public QcService(RuleEngine ruleEngine, ...)
{
    _ruleEngine = ruleEngine;
    // Level 1-4 规则不再逐一 new
}

public async Task<AjaxResult> ExecuteQcAsync(QcRequest request)
{
    // Level 0: 预处理（保留）
    // Level 1-4: 统一由 RuleEngine 处理
    var issues = _ruleEngine.Execute(request);

    // UnitFormatRule 单独执行
    issues.AddRange(_unitFormatRule.Check(request));

    // Skill Squad 和 Scoring 不变
}
```

## 迁移步骤

1. **创建 `SeedRules.cs`** — 一次性脚本，读取各现有规则类的硬编码数组，写入 `knowledge/rules.db`
2. **手动执行 SeedRules** — `dotnet run --seed-rules`
3. **验证 rules.db 数据完整**
4. **删除 SeedRules.cs**
5. **实现 RuleEngine + SqliteRuleStore + TrieMatcher + RuleExecutor**
6. **更新 QcService**，替换规则调用为 `_ruleEngine.Execute(request)`
7. **更新测试** — 每个废弃规则类的测试迁移为 `RuleEngineTests`
8. **删除废弃的规则类**（保留 UnitFormatRule）
9. **确认 rules.db 纳入项目**（csproj 包含 `knowledge/rules.db` 作为 Content）

## 文件清单

### 新增

| 文件 | 说明 |
|------|------|
| `knowledge/rules.db` | SQLite 规则数据库 |
| `src/Services/RuleEngine.cs` | 引擎入口，加载+执行 |
| `src/Services/SqliteRuleStore.cs` | SQLite 读写封装 |
| `src/Services/TrieMatcher.cs` | Trie 构建+多模式匹配 |
| `src/Services/RuleExecutor.cs` | 按规则类型分发执行 |
| `src/Models/RuleDef.cs` | 规则定义 model |
| `tests/.../RuleEngineTests.cs` | 引擎集成测试 |

### 删除（迁移确认后，共 18 个文件）

| 文件 | 迁移目标 |
|------|----------|
| `src/Services/Rules/GenderConflictRule.cs` | → keyword_negation |
| `src/Services/Rules/AgeConflictRule.cs` | → keyword_age |
| `src/Services/Rules/DirectionConflictRule.cs` | → direction_compare |
| `src/Services/Rules/DeviceConflictRule.cs` | → keyword_device |
| `src/Services/Rules/ScanEnhanceConflictRule.cs` | → keyword_scan |
| `src/Services/Rules/CriticalSignRule.cs` | → keyword_negation |
| `src/Services/Rules/Level1/PhraseTypoRule.cs` | → keyword_replace |
| `src/Services/Rules/Level1/DuplicateCharRule.cs` | → regex_duplicate |
| `src/Services/Rules/Level1/SentencePunctuationRule.cs` | → sentence_check |
| `src/Services/Rules/Level1/PatientInfoRule.cs` | → field_validation |
| `src/Services/Rules/Level1/TerminologyStandardRule.cs` | → keyword_replace |
| `src/Services/Rules/Level2/ColloquialTermRule.cs` | → keyword_replace |
| `src/Services/Rules/Level2/AnatomyTermRule.cs` | → keyword_replace |
| `src/Services/Rules/Level2/LesionCompletenessRule.cs` | → cross_section |
| `src/Services/Rules/Level2/RadsClassificationRule.cs` | → cross_section |
| `src/Services/Rules/Level2/FindingsImpressionConsistencyRule.cs` | → cross_section |
| `src/Services/Rules/Level2/ComparisonDescriptionRule.cs` | → cross_section |
| `src/Services/Rules/Level2/AdviceConsistencyRule.cs` | → cross_section |
| `src/Services/Rules/Level1/UnitFormatRule.cs` | **保留，不迁移** |

### 修改

| 文件 | 说明 |
|------|------|
| `src/Services/QcService.cs` | 替换规则调用为 RuleEngine |
| `src/Program.cs` | 注册 RuleEngine 单例 |
| `src/Agent_QC.csproj` | 添加 `rules.db` Content |
| `src/Agent_QC.csproj` | 添加 `Microsoft.Data.Sqlite` 包引用 |

## 约束

- 测量单位冲突检测（UnitFormatRule）保持现有实现不变
- NegationDetector 保留并继续使用
- SectionParser 保留（Level 0 预处理）
- JiebaSegmenter 保留（Level 0 预处理）
- ScoringEngine 保留
- Skill Squad + QA Arbiter 保留
- 变更后测试覆盖率 ≥ 80%
