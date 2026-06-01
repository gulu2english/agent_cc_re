# SQLite+Trie 统一规则引擎实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 18 条硬编码规则迁移为 SQLite 存储 + Trie 多模式匹配的统一 RuleEngine，解决 Issue #6。

**Architecture:** RuleEngine 单例在 Program.cs 启动时从 `knowledge/rules.db` 加载规则到内存，构建 Trie。QcService 调用 `_ruleEngine.Execute(request)` 替代所有独立规则类。9 种规则类型在 RuleExecutor 中按 type 分发。

**Tech Stack:** C#/.NET 8, Microsoft.Data.Sqlite, xUnit + Moq

---

## 文件结构

| 文件 | 职责 |
|------|------|
| `src/Models/RuleDef.cs` | 规则定义 DTO + RuleKeyword DTO |
| `src/Services/SqliteRuleStore.cs` | SQLite 读写：LoadAll() → List<RuleDef> |
| `src/Services/TrieMatcher.cs` | Trie 构建 + FindAll(text) → hits |
| `src/Services/RuleExecutor.cs` | 9 种 rule_type dispatch → List<QcIssueDto> |
| `src/Services/RuleEngine.cs` | 入口：Init(dbPath) → Execute(request)，组合上述三者 |
| `knowledge/rules.db` | SQLite 数据库，预填充所有规则 |
| `src/Program.cs` (改) | 注册 RuleEngine 单例 |
| `src/Services/QcService.cs` (改) | 替换规则调用为 RuleEngine |
| `src/Agent_QC.csproj` (改) | 添加 Microsoft.Data.Sqlite |
| `tests/.../RuleEngineTests.cs` | 引擎集成测试（覆盖所有规则类型） |
| `scripts/SeedRules/` (临时) | 一次性迁移脚本，用完删除 |

---

### Task 1: RuleDef 数据模型

**Files:**
- Create: `src/Agent_QC/src/Models/RuleDef.cs`

- [ ] **Step 1: 创建 RuleDef 和 RuleKeyword 记录类型**

```csharp
namespace Agent_QC.Models;

/// <summary>规则定义（从 SQLite rule_def 表加载）。</summary>
public record RuleDef
{
    public int Id { get; init; }
    public string RuleType { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Severity { get; init; } = "warning";
    public string? ParamsJson { get; init; }
    public string? Description { get; init; }
    public bool IsActive { get; init; } = true;
    public int SortOrder { get; init; }

    /// <summary>关联的关键词列表。</summary>
    public List<RuleKeyword> Keywords { get; init; } = new();
}

/// <summary>规则关键词（从 SQLite rule_keyword 表加载）。</summary>
public record RuleKeyword
{
    public int Id { get; init; }
    public int RuleId { get; init; }
    public string Keyword { get; init; } = string.Empty;
    public int KeywordLen { get; init; }
    public int Priority { get; init; }
    public bool IsExclude { get; init; }
    public string? ExtraData { get; init; }
}
```

- [ ] **Step 2: 编译检查**

```bash
dotnet build src/Agent_QC/ --configuration Release
```

---

### Task 2: SqliteRuleStore

**Files:**
- Create: `src/Agent_QC/src/Services/SqliteRuleStore.cs`
- Modify: `src/Agent_QC/src/Agent_QC.csproj`

- [ ] **Step 1: 添加 Microsoft.Data.Sqlite NuGet 包**

```bash
cd src/Agent_QC && dotnet add src/Agent_QC.csproj package Microsoft.Data.Sqlite
```

- [ ] **Step 2: 实现 SqliteRuleStore.LoadAll()**

```csharp
using Microsoft.Data.Sqlite;
using Agent_QC.Models;

namespace Agent_QC.Services;

public class SqliteRuleStore
{
    private readonly string _dbPath;

    public SqliteRuleStore(string dbPath) => _dbPath = dbPath;

    public List<RuleDef> LoadAll()
    {
        var rules = new List<RuleDef>();
        var keywords = new Dictionary<int, List<RuleKeyword>>();

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        // 加载关键词
        using var kwCmd = new SqliteCommand(
            "SELECT id, rule_id, keyword, keyword_len, priority, is_exclude, extra_data FROM rule_keyword ORDER BY keyword_len DESC", conn);
        using var kwReader = kwCmd.ExecuteReader();
        while (kwReader.Read())
        {
            var kw = new RuleKeyword
            {
                Id = kwReader.GetInt32(0),
                RuleId = kwReader.GetInt32(1),
                Keyword = kwReader.GetString(2),
                KeywordLen = kwReader.GetInt32(3),
                Priority = kwReader.GetInt32(4),
                IsExclude = kwReader.GetBoolean(5),
                ExtraData = kwReader.IsDBNull(6) ? null : kwReader.GetString(6),
            };
            if (!keywords.ContainsKey(kw.RuleId))
                keywords[kw.RuleId] = new List<RuleKeyword>();
            keywords[kw.RuleId].Add(kw);
        }

        // 加载规则定义
        using var ruleCmd = new SqliteCommand(
            "SELECT id, rule_type, name, category, severity, params_json, description, is_active, sort_order FROM rule_def WHERE is_active = 1 ORDER BY sort_order", conn);
        using var ruleReader = ruleCmd.ExecuteReader();
        while (ruleReader.Read())
        {
            var rule = new RuleDef
            {
                Id = ruleReader.GetInt32(0),
                RuleType = ruleReader.GetString(1),
                Name = ruleReader.GetString(2),
                Category = ruleReader.GetString(3),
                Severity = ruleReader.GetString(4),
                ParamsJson = ruleReader.IsDBNull(5) ? null : ruleReader.GetString(5),
                Description = ruleReader.IsDBNull(6) ? null : ruleReader.GetString(6),
                IsActive = ruleReader.GetBoolean(7),
                SortOrder = ruleReader.GetInt32(8),
                Keywords = keywords.GetValueOrDefault(ruleReader.GetInt32(0), new List<RuleKeyword>()),
            };
            rules.Add(rule);
        }

        return rules;
    }
}
```

- [ ] **Step 3: 编译检查**

```bash
dotnet build src/Agent_QC/ --configuration Release
```

---

### Task 3: TrieMatcher

**Files:**
- Create: `src/Agent_QC/src/Services/TrieMatcher.cs`

- [ ] **Step 1: 实现 TrieNode, TrieMatcher**

```csharp
using Agent_QC.Models;

namespace Agent_QC.Services;

public class TrieNode
{
    public Dictionary<char, TrieNode> Children { get; } = new();
    public List<int> KeywordIds { get; } = new(); // 该节点结尾的 rule_keyword id
}

/// <summary>Trie 多模式匹配器 — O(n) 单遍扫描。</summary>
public class TrieMatcher
{
    private readonly TrieNode _root = new();
    private readonly Dictionary<int, RuleKeyword> _keywordById = new();

    /// <summary>从所有规则的关键词构建 Trie。长词优先插入，避免短词覆盖长词。</summary>
    public void Build(List<RuleDef> rules)
    {
        _root.Children.Clear();
        _keywordById.Clear();

        var allKeywords = rules
            .SelectMany(r => r.Keywords)
            .Where(k => k.IsExclude == false) // 排除模式不进 Trie（仅用于 keyword_negation 排除检查）
            .OrderByDescending(k => k.KeywordLen)
            .ToList();

        foreach (var kw in allKeywords)
        {
            _keywordById[kw.Id] = kw;
            var node = _root;
            foreach (char c in kw.Keyword)
            {
                if (!node.Children.TryGetValue(c, out var child))
                {
                    child = new TrieNode();
                    node.Children[c] = child;
                }
                node = child;
            }
            node.KeywordIds.Add(kw.Id);
        }
    }

    /// <summary>在 text 中查找所有匹配的关键词。</summary>
    public List<TrieHit> FindAll(string text)
    {
        var hits = new List<TrieHit>();
        if (string.IsNullOrEmpty(text)) return hits;

        int i = 0;
        while (i < text.Length)
        {
            var node = _root;
            int j = i;
            int lastMatchEnd = -1;
            int lastMatchKwId = -1;

            while (j < text.Length && node.Children.TryGetValue(text[j], out var child))
            {
                node = child;
                j++;
                if (node.KeywordIds.Count > 0)
                {
                    // 使用最长的一个 keyword_id（长词优先，取第一个）
                    lastMatchEnd = j;
                    lastMatchKwId = node.KeywordIds[0];
                }
            }

            if (lastMatchEnd > i)
            {
                var kw = _keywordById[lastMatchKwId];
                hits.Add(new TrieHit(kw.Keyword, i, lastMatchKwId, kw.RuleId));
                i = lastMatchEnd; // 跳过已匹配区域
            }
            else
            {
                i++;
            }
        }

        return hits;
    }

    /// <summary>简单包含检查，用于 keyword_device 的禁止词判断。</summary>
    public bool ContainsAny(string text, IEnumerable<string> terms)
    {
        return terms.Any(term =>
            FindAll(text).Any(h => h.Keyword == term));
    }
}

public record TrieHit(string Keyword, int StartPos, int KeywordId, int RuleId);
```

- [ ] **Step 2: 编译检查**

```bash
dotnet build src/Agent_QC/ --configuration Release
```

---

### Task 4: RuleExecutor

**Files:**
- Create: `src/Agent_QC/src/Services/RuleExecutor.cs`

- [ ] **Step 1: 实现 RuleExecutor 完整 9 类型分发**

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using Agent_QC.Models;

namespace Agent_QC.Services;

public class RuleExecutor
{
    private readonly TrieMatcher _trie;
    private readonly NegationDetector _negationDetector = new();

    // cross_section / regex_duplicate / sentence_check 中会用到的固定关键词不走 Trie，
    // 而是存在 params_json 中，在各自的 handler 中解析使用。

    public RuleExecutor(TrieMatcher trie) => _trie = trie;

    public List<QcIssueDto> Execute(QcRequest request, List<RuleDef> rules, Dictionary<int, RuleKeyword> keywordById)
    {
        var issues = new List<QcIssueDto>();
        var fullText = (request.Findings ?? "") + (request.Impression ?? "");
        var findings = request.Findings ?? "";
        var impression = request.Impression ?? "";

        // Trie 单遍扫描全文本
        var trieHits = _trie.FindAll(fullText);
        var hitsByRuleId = trieHits.GroupBy(h => h.RuleId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var rule in rules.Where(r => r.IsActive).OrderBy(r => r.SortOrder))
        {
            var ruleHits = hitsByRuleId.GetValueOrDefault(rule.Id, new List<TrieHit>());

            switch (rule.RuleType)
            {
                case "keyword_negation":
                    HandleKeywordNegation(rule, ruleHits, fullText, issues);
                    break;
                case "keyword_age":
                    HandleKeywordAge(rule, ruleHits, request, issues, keywordById);
                    break;
                case "keyword_device":
                    HandleKeywordDevice(rule, request, issues);
                    break;
                case "keyword_scan":
                    HandleKeywordScan(rule, ruleHits, request, issues);
                    break;
                case "keyword_replace":
                    HandleKeywordReplace(rule, ruleHits, issues, keywordById);
                    break;
                case "direction_compare":
                    HandleDirectionCompare(findings, impression, issues);
                    break;
                case "cross_section":
                    HandleCrossSection(rule, findings, impression, fullText, issues);
                    break;
                case "regex_duplicate":
                    HandleRegexDuplicate(rule, fullText, issues);
                    break;
                case "sentence_check":
                    HandleSentenceCheck(request, issues);
                    break;
                case "field_validation":
                    HandleFieldValidation(request, issues);
                    break;
            }
        }

        return issues;
    }

    // ── keyword_negation ──────────────────────────────

    private void HandleKeywordNegation(RuleDef rule, List<TrieHit> hits, string fullText, List<QcIssueDto> issues)
    {
        var excludeKeywords = rule.Keywords.Where(k => k.IsExclude).Select(k => k.Keyword).ToList();

        foreach (var hit in hits)
        {
            if (_negationDetector.IsNegated(fullText, hit.Keyword)) continue;
            if (HasExcludePattern(fullText, hit.Keyword, hit.StartPos, excludeKeywords)) continue;

            issues.Add(new QcIssueDto
            {
                IssueType = rule.Name,
                Severity = rule.Severity,
                Description = rule.Description?.Replace("{keyword}", hit.Keyword) ?? $"检测到「{hit.Keyword}」",
            });
        }
    }

    private static bool HasExcludePattern(string text, string keyword, int kwIdx, List<string> excludePatterns)
    {
        if (excludePatterns.Count == 0) return false;
        int start = Math.Max(0, kwIdx - 5);
        int end = Math.Min(text.Length, kwIdx + keyword.Length + 10);
        var context = text[start..end];
        return excludePatterns.Any(p => context.Contains(p, StringComparison.Ordinal));
    }

    // ── keyword_age ───────────────────────────────────

    private void HandleKeywordAge(RuleDef rule, List<TrieHit> hits, QcRequest request,
        List<QcIssueDto> issues, Dictionary<int, RuleKeyword> keywordById)
    {
        if (request.PatientAge == null) return;
        var age = request.PatientAge.Value;

        foreach (var hit in hits)
        {
            var extraData = keywordById.GetValueOrDefault(hit.KeywordId)?.ExtraData;
            if (string.IsNullOrEmpty(extraData)) continue;
            var parts = extraData.Split('-');
            if (parts.Length != 2) continue;
            if (!int.TryParse(parts[0], out var min) || !int.TryParse(parts[1], out var max)) continue;
            if (age < min || age > max) continue;

            issues.Add(new QcIssueDto
            {
                IssueType = rule.Name,
                Severity = rule.Severity,
                Description = $"患者年龄{age}岁，出现「{hit.Keyword}」需确认（一般{min}-{max}岁不常见）",
            });
        }
    }

    // ── keyword_device ────────────────────────────────

    private void HandleKeywordDevice(RuleDef rule, QcRequest request, List<QcIssueDto> issues)
    {
        if (string.IsNullOrWhiteSpace(request.ExamDevice)) return;
        if (string.IsNullOrWhiteSpace(rule.ParamsJson)) return;

        var deviceMapping = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(rule.ParamsJson);
        if (deviceMapping == null) return;

        var fullText = (request.Findings ?? "") + (request.Impression ?? "");

        foreach (var (deviceKey, bannedTerms) in deviceMapping)
        {
            if (!request.ExamDevice.Contains(deviceKey, StringComparison.Ordinal)) continue;

            foreach (var term in bannedTerms)
            {
                if (fullText.Contains(term, StringComparison.Ordinal))
                {
                    issues.Add(new QcIssueDto
                    {
                        IssueType = rule.Name,
                        Severity = rule.Severity,
                        Description = $"检查设备为{request.ExamDevice}，但报告中出现「{term}」",
                    });
                }
            }
        }
    }

    // ── keyword_scan ──────────────────────────────────

    private void HandleKeywordScan(RuleDef rule, List<TrieHit> hits, QcRequest request, List<QcIssueDto> issues)
    {
        if (string.IsNullOrWhiteSpace(request.ExamMethod)) return;
        if (string.IsNullOrWhiteSpace(rule.ParamsJson)) return;

        using var doc = JsonDocument.Parse(rule.ParamsJson);
        var root = doc.RootElement;
        var triggerMethod = root.GetProperty("trigger_method").GetString() ?? "";

        if (!request.ExamMethod.Contains(triggerMethod, StringComparison.Ordinal)) return;

        foreach (var hit in hits)
        {
            issues.Add(new QcIssueDto
            {
                IssueType = rule.Name,
                Severity = rule.Severity,
                Description = $"检查方式为{triggerMethod}，但报告中出现增强描述「{hit.Keyword}」",
            });
            break;
        }
    }

    // ── keyword_replace ───────────────────────────────

    private void HandleKeywordReplace(RuleDef rule, List<TrieHit> hits, List<QcIssueDto> issues,
        Dictionary<int, RuleKeyword> keywordById)
    {
        string subType = "unknown";
        if (!string.IsNullOrWhiteSpace(rule.ParamsJson))
        {
            using var doc = JsonDocument.Parse(rule.ParamsJson);
            subType = doc.RootElement.TryGetProperty("sub_type", out var st) ? st.GetString() ?? "unknown" : "unknown";
        }

        foreach (var hit in hits)
        {
            var suggestion = keywordById.GetValueOrDefault(hit.KeywordId)?.ExtraData ?? "建议修改";
            issues.Add(new QcIssueDto
            {
                IssueType = rule.Category == "level1_text" ? "text_error" : "terminology_error",
                SubType = subType,
                Severity = rule.Severity,
                OriginalText = hit.Keyword,
                SuggestedText = suggestion,
                Description = BuildReplaceDescription(subType, hit.Keyword, suggestion),
                Suggestion = BuildReplaceSuggestion(subType, hit.Keyword, suggestion),
            });
        }
    }

    private static string BuildReplaceDescription(string subType, string keyword, string suggestion) => subType switch
    {
        "phrase_typo" => $"疑似错别字「{keyword}」，应为「{suggestion}」",
        "anatomy_nonstandard" => $"解剖术语不标准「{keyword}」，建议使用「{suggestion}」",
        "colloquial" => $"报告中出现口语化/非客观描述「{keyword}」",
        _ => $"「{keyword}」应为规范术语「{suggestion}」",
    };

    private static string BuildReplaceSuggestion(string subType, string keyword, string suggestion) => subType switch
    {
        "phrase_typo" => $"请将「{keyword}」更正为「{suggestion}」",
        "anatomy_nonstandard" => $"请使用国际通用解剖命名「{suggestion}」代替「{keyword}」",
        "colloquial" => suggestion,
        _ => $"建议使用「{suggestion}」代替「{keyword}」",
    };

    // ── direction_compare ─────────────────────────────

    private static readonly string[] LeftWords =
        { "左侧", "左叶", "左侧壁", "左肺", "左肾", "左上", "左下", "左前", "左后" };
    private static readonly string[] RightWords =
        { "右侧", "右叶", "右侧壁", "右肺", "右肾", "右上", "右下", "右前", "右后" };

    private void HandleDirectionCompare(string findings, string impression, List<QcIssueDto> issues)
    {
        bool findingsLeft = LeftWords.Any(f => findings.Contains(f, StringComparison.Ordinal));
        bool findingsRight = RightWords.Any(f => findings.Contains(f, StringComparison.Ordinal));
        bool impressionLeft = LeftWords.Any(f => impression.Contains(f, StringComparison.Ordinal));
        bool impressionRight = RightWords.Any(f => impression.Contains(f, StringComparison.Ordinal));

        if (findingsLeft && impressionRight && !impressionLeft)
        {
            issues.Add(new QcIssueDto
            {
                IssueType = "direction_conflict",
                Severity = "warning",
                Description = "影像所见为左侧，诊断结论为右侧，请确认方位是否准确",
            });
        }
        if (findingsRight && impressionLeft && !impressionRight)
        {
            issues.Add(new QcIssueDto
            {
                IssueType = "direction_conflict",
                Severity = "warning",
                Description = "影像所见为右侧，诊断结论为左侧，请确认方位是否准确",
            });
        }
    }

    // ── cross_section ─────────────────────────────────

    private void HandleCrossSection(RuleDef rule, string findings, string impression, string fullText, List<QcIssueDto> issues)
    {
        if (string.IsNullOrWhiteSpace(rule.ParamsJson)) return;

        using var doc = JsonDocument.Parse(rule.ParamsJson);
        var root = doc.RootElement;

        var positiveSet = root.TryGetProperty("positive_set", out var ps)
            ? ps.EnumerateArray().Select(e => e.GetString()!).Where(s => s != null).ToList()
            : new List<string>();
        var negativeSet = root.TryGetProperty("negative_set", out var ns)
            ? ns.EnumerateArray().Select(e => e.GetString()!).Where(s => s != null).ToList()
            : new List<string>();
        var matchMode = root.TryGetProperty("match_mode", out var mm) ? mm.GetString() ?? "" : "";
        var searchSection = root.TryGetProperty("search_section", out var ss) ? ss.GetString() ?? "full_text" : "full_text";
        var secondarySection = root.TryGetProperty("secondary_section", out var ss2) ? ss2.GetString() ?? "impression" : "impression";

        var section = searchSection switch
        {
            "findings" => findings,
            "impression" => impression,
            _ => fullText,
        };
        var section2 = secondarySection switch
        {
            "findings" => findings,
            "impression" => impression,
            _ => impression,
        };

        bool hasA = positiveSet.Count == 0 || positiveSet.Any(kw => section.Contains(kw, StringComparison.Ordinal));
        bool hasB = negativeSet.Count == 0 || negativeSet.Any(kw => section2.Contains(kw, StringComparison.Ordinal));

        if (matchMode == "if_A_then_B" && hasA && !hasB)
        {
            issues.Add(new QcIssueDto
            {
                IssueType = rule.Category == "level2_semantic" ? "terminology_error" : rule.Name,
                SubType = rule.Name,
                Severity = rule.Severity,
                Description = rule.Description ?? $"条件不满足",
                Suggestion = root.TryGetProperty("suggestion", out var sug) ? sug.GetString() : null,
            });
        }

        if (matchMode == "if_A_not_B" && hasA && !hasB)
        {
            issues.Add(new QcIssueDto
            {
                IssueType = rule.Category == "level2_semantic" ? "terminology_error" : rule.Name,
                SubType = rule.Name,
                Severity = rule.Severity,
                Description = rule.Description ?? $"条件不满足",
                Suggestion = root.TryGetProperty("suggestion", out var sug) ? sug.GetString() : null,
            });
        }

        if (matchMode == "severity_compare" && hasA && hasB)
        {
            issues.Add(new QcIssueDto
            {
                IssueType = "semantic_conflict",
                SubType = rule.Name,
                Severity = rule.Severity,
                Description = rule.Description ?? $"程度矛盾",
                Suggestion = root.TryGetProperty("suggestion", out var sug) ? sug.GetString() : null,
            });
        }
    }

    // ── regex_duplicate ───────────────────────────────

    [GeneratedRegex(@"([\p{IsCJKUnifiedIdeographs}])\1{1,}", RegexOptions.Compiled)]
    private static partial Regex DupPattern();

    private void HandleRegexDuplicate(RuleDef rule, string fullText, List<QcIssueDto> issues)
    {
        var validDuplicates = new HashSet<char>();
        if (!string.IsNullOrWhiteSpace(rule.ParamsJson))
        {
            using var doc = JsonDocument.Parse(rule.ParamsJson);
            if (doc.RootElement.TryGetProperty("valid_duplicates", out var vd))
                foreach (var c in vd.EnumerateArray())
                    if (c.GetString() is { Length: > 0 } s) validDuplicates.Add(s[0]);
        }

        foreach (Match m in DupPattern().Matches(fullText))
        {
            var dupChar = m.Value[0];
            if (validDuplicates.Contains(dupChar)) continue;

            issues.Add(new QcIssueDto
            {
                IssueType = "text_error",
                SubType = "duplicate_char",
                Severity = rule.Severity,
                OriginalText = m.Value,
                SuggestedText = dupChar.ToString(),
                Description = $"疑似重复字「{m.Value}」",
                Suggestion = $"请检查是否误写了两次「{dupChar}」",
            });
        }
    }

    // ── sentence_check ────────────────────────────────

    private static readonly HashSet<char> ValidEndings = new() { '。', '）', '；', '：', '！', '？', '.' };
    private static readonly char[] ListStarters = { '-', '•', '·' };

    private void HandleSentenceCheck(QcRequest request, List<QcIssueDto> issues)
    {
        CheckSection(request.Findings ?? "", "findings", issues);
        CheckSection(request.Impression ?? "", "impression", issues);
    }

    private static void CheckSection(string text, string location, List<QcIssueDto> issues)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var sentences = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var sentence in sentences)
        {
            var trimmed = sentence.Trim();
            if (trimmed.Length < 4) continue;
            if (ListStarters.Any(c => trimmed.StartsWith(c))) continue;
            if (char.IsDigit(trimmed[0])) continue;

            if (!ValidEndings.Contains(trimmed[^1]))
            {
                issues.Add(new QcIssueDto
                {
                    IssueType = "text_error",
                    SubType = "missing_period",
                    Severity = "warning",
                    OriginalText = trimmed.Length > 50 ? trimmed[..50] + "..." : trimmed,
                    Description = "句末缺少标点符号（。；：等）",
                    Suggestion = "请在句末添加句号（。）",
                    Location = location,
                });
                break;
            }
        }
    }

    // ── field_validation ──────────────────────────────

    private void HandleFieldValidation(QcRequest request, List<QcIssueDto> issues)
    {
        if (!string.IsNullOrWhiteSpace(request.PatientGender)
            && request.PatientGender != "男" && request.PatientGender != "女")
        {
            issues.Add(new QcIssueDto
            {
                IssueType = "completeness_error",
                SubType = "invalid_field",
                Severity = "error",
                Description = $"患者性别「{request.PatientGender}」不在标准范围内（男/女）",
                Suggestion = "性别应为「男」或「女」",
            });
        }

        if (request.PatientAge is < 0 or > 150)
        {
            issues.Add(new QcIssueDto
            {
                IssueType = "completeness_error",
                SubType = "invalid_field",
                Severity = "error",
                Description = $"患者年龄 {request.PatientAge} 不在合理范围（0-150）",
                Suggestion = "请核实患者年龄",
            });
        }

        if (string.IsNullOrWhiteSpace(request.ExamPart))
        {
            issues.Add(new QcIssueDto
            {
                IssueType = "completeness_error",
                SubType = "missing_required_field",
                Severity = "warning",
                Description = "检查部位不能为空",
                Suggestion = "请填写检查部位",
            });
        }

        if (string.IsNullOrWhiteSpace(request.ExamDevice))
        {
            issues.Add(new QcIssueDto
            {
                IssueType = "completeness_error",
                SubType = "missing_required_field",
                Severity = "warning",
                Description = "检查设备不能为空",
                Suggestion = "请填写检查设备",
            });
        }
    }
}
```

注意：`TrieHit.GetExtraData(rule)` 需要在 Task 5 的 RuleEngine 中扩展。或者在 TrieHit 记录中直接存储 `ExtraData`。为简单起见，在 RuleEngine 层做 keywordId → RuleKeyword 的映射。

- [ ] **Step 2: 编译检查**

```bash
dotnet build src/Agent_QC/ --configuration Release
```

---

### Task 5: RuleEngine 入口

**Files:**
- Create: `src/Agent_QC/src/Services/RuleEngine.cs`

- [ ] **Step 1: 实现 RuleEngine**

```csharp
using Agent_QC.Models;

namespace Agent_QC.Services;

public class RuleEngine
{
    private readonly SqliteRuleStore _store;
    private readonly TrieMatcher _trie;
    private readonly RuleExecutor _executor;

    private List<RuleDef> _rules = new();
    private bool _initialized;

    public RuleEngine(string dbPath)
    {
        _store = new SqliteRuleStore(dbPath);
        _trie = new TrieMatcher();
        _executor = new RuleExecutor(_trie);
    }

    /// <summary>从 SQLite 加载所有规则并构建 Trie。</summary>
    public void Initialize()
    {
        if (_initialized) return;
        _rules = _store.LoadAll();
        _trie.Build(_rules);
        _initialized = true;
    }

    /// <summary>执行全部规则，返回问题列表。</summary>
    public List<QcIssueDto> Execute(QcRequest request)
    {
        if (!_initialized) Initialize();

        var keywordById = _rules.SelectMany(r => r.Keywords)
            .ToDictionary(k => k.Id, k => k);

        return _executor.Execute(request, _rules, keywordById);
    }
}
```

- [ ] **Step 2: 编译检查**

```bash
dotnet build src/Agent_QC/ --configuration Release
```

---

### Task 6: SeedRules 一次性迁移脚本

**Files:**
- Create: `scripts/SeedRules/SeedRules.csproj`
- Create: `scripts/SeedRules/Program.cs`

- [ ] **Step 1: 创建临时项目文件**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.7" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 编写迁移脚本**

脚本从现有规则类的硬编码数据中提取全部关键词，写入 `knowledge/rules.db`。对照 spec 中的 9 种规则类型，为每个规则类生成 rule_def + rule_keyword 行。

具体数据提取：

```
GenderConflictRule: rule_type=keyword_negation
  keywords: FemaleParts[] + MaleParts[] (非 exclude)
  exclude: "切除术后","术后复查","术后改变","摘除术后","未见显示","未见明确"
  severity: critical, category: level3_logic

CriticalSignRule: rule_type=keyword_negation
  keywords: CriticalSigns[] (34个)  
  severity: critical, category: level4_critical

AgeConflictRule: rule_type=keyword_age
  keywords + extra_data: AgeRules[] (keyword, "min-max" format)
  severity: per-rule, category: level3_logic

DeviceConflictRule: rule_type=keyword_device
  params_json: {"device_mapping": {CT:[...], MRI:[...], DR:[...], 超声:[...]}}
  severity: error, category: level3_logic

ScanEnhanceConflictRule: rule_type=keyword_scan
  keywords: EnhanceDescriptions[]
  params_json: {"trigger_method":"平扫"}
  severity: error, category: level3_logic

PhraseTypoRule: rule_type=keyword_replace
  keywords: PhraseTypos.Keys, extra_data: PhraseTypos.Values
  params_json: {"sub_type":"phrase_typo"}
  severity: error, category: level1_text

TerminologyStandardRule: rule_type=keyword_replace
  keywords: StandardTerms.Keys, extra_data: StandardTerms.Values
  params_json: {"sub_type":"terminology"}
  severity: warning, category: level1_text

AnatomyTermRule: rule_type=keyword_replace
  keywords: AnatomyTerms.Keys, extra_data: AnatomyTerms.Values
  params_json: {"sub_type":"anatomy_nonstandard"}
  severity: warning, category: level2_semantic

ColloquialTermRule: rule_type=keyword_replace
  keywords: ColloquialTerms.word, extra_data: ColloquialTerms.suggestion
  params_json: {"sub_type":"colloquial"}
  severity: warning, category: level2_semantic
```

以及 9 种非 Trie 规则类型的 rule_def 行（direction_compare, cross_section x5, regex_duplicate, sentence_check, field_validation）。

完整脚本代码见实现时补充（数据量较大）。

- [ ] **Step 3: 执行迁移并生成 rules.db**

```bash
cd scripts/SeedRules && dotnet run
```

- [ ] **Step 4: 验证 rules.db**

```bash
sqlite3 knowledge/rules.db "SELECT rule_type, name, COUNT(rk.id) FROM rule_def rd LEFT JOIN rule_keyword rk ON rk.rule_id=rd.id GROUP BY rd.id ORDER BY rd.sort_order;"
```

- [ ] **Step 5: 删除迁移脚本目录**

```bash
rm -rf scripts/SeedRules
```

- [ ] **Step 6: 提交 rules.db**

```bash
git add knowledge/rules.db && git commit -m "feat(rule-engine): add pre-populated rules.db"
```

---

### Task 7: QcService 集成

**Files:**
- Modify: `src/Agent_QC/src/Services/QcService.cs`
- Modify: `src/Agent_QC/src/Program.cs`

- [ ] **Step 1: 修改 QcService 使用 RuleEngine**

保留所有 Level 0 预处理、Skill Squad、Scoring 不变。Level 1-4 替换为 RuleEngine：

```csharp
using System.Diagnostics;
using Agent_QC.Models;
using Agent_QC.Services.Rules.Level1;

namespace Agent_QC.Services;

public class QcService : IQcService
{
    // Level 0: 预处理
    private readonly SectionParser _sectionParser = new();
    private readonly JiebaSegmenter _jieba;

    // 规则引擎 (替代全部 Level 1-4 独立规则类)
    private readonly RuleEngine _ruleEngine;

    // 测量单位检测（保留不变）
    private readonly UnitFormatRule _unitFormatRule = new();

    // Hermes Skill Squad (不变)
    private readonly IVllmClient _vllm;
    private readonly SkillRegistry _skillRegistry;
    private readonly HermesOrchestrator _orchestrator;
    private readonly QaArbiter _arbiter;

    private readonly ScoringEngine _scoringEngine = new();

    public QcService(RuleEngine ruleEngine, IVllmClient? vllm = null,
        SkillRegistry? skillRegistry = null, JiebaSegmenter? jieba = null)
    {
        _ruleEngine = ruleEngine;
        _vllm = vllm ?? new VllmClient(new HttpClient(), "http://localhost:8100");
        _skillRegistry = skillRegistry ?? new SkillRegistry();
        _jieba = jieba ?? new JiebaSegmenter("knowledge/jieba_medical_dict.txt");
        _orchestrator = new HermesOrchestrator(_vllm, _skillRegistry);
        _arbiter = new QaArbiter();
    }

    public async Task<AjaxResult> ExecuteQcAsync(QcRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ReportId))
            return AjaxResult.Error(400, "ReportId 不能为空");

        var sw = Stopwatch.StartNew();
        var issues = new List<QcIssueDto>();

        // ── Level 0: 预处理 ──
        request.SegmentedFindings = _jieba.Segment(request.Findings ?? "");
        request.SegmentedImpression = _jieba.Segment(request.Impression ?? "");

        // ── Level 1-4: 统一规则引擎 ──
        issues.AddRange(_ruleEngine.Execute(request));

        // 测量单位检测（保留独立）
        issues.AddRange(_unitFormatRule.Check(request));

        // ── Skill Squad (不变) ──
        if (_vllm.Health == VllmHealthStatus.Healthy)
        {
            var ruleIssues = new List<QcIssueDto>(issues);
            var skillResults = await _orchestrator.DispatchAsync(request, ruleIssues, CancellationToken.None);
            if (skillResults.Count > 0)
                issues = await _arbiter.ArbitrateAsync(ruleIssues, skillResults);
        }

        sw.Stop();

        var response = new QcResponse
        {
            ReportId = request.ReportId,
            QcLevel = "L1+L2+L3+L4",
            Issues = issues,
            ProcessTimeMs = (int)sw.ElapsedMilliseconds,
        };
        _scoringEngine.Calculate(response);

        response.Summary = issues.Count == 0
            ? "未发现问题"
            : $"发现 {issues.Count} 个问题";

        return AjaxResult.Success(response);
    }
}
```

- [ ] **Step 2: 修改 Program.cs 注册 RuleEngine**

```csharp
// 在 jieba 注册之后、QC 服务之前插入：
var rulesDbPath = Path.Combine(AppContext.BaseDirectory, "knowledge", "rules.db");
var ruleEngine = new RuleEngine(rulesDbPath);
ruleEngine.Initialize();
builder.Services.AddSingleton(ruleEngine);
```

- [ ] **Step 3: 编译检查**

```bash
dotnet build src/Agent_QC/ --configuration Release
```

---

### Task 8: 删除废弃规则类和更新测试

**Files:**
- Delete: `src/Agent_QC/src/Services/Rules/GenderConflictRule.cs` 等 18 个
- Delete: `src/Agent_QC/tests/UnitTests/Services/Rules/GenderConflictRuleTests.cs` 等 18 个测试
- Create: `src/Agent_QC/tests/UnitTests/Services/RuleEngineTests.cs`
- Create: `src/Agent_QC/tests/UnitTests/Services/TrieMatcherTests.cs`

- [ ] **Step 1: 编写 TrieMatcher 单元测试**

```csharp
using Xunit;
using Agent_QC.Models;
using Agent_QC.Services;

namespace Agent_QC.Tests.UnitTests.Services;

public class TrieMatcherTests
{
    [Fact]
    public void 单关键词匹配()
    {
        var rules = new List<RuleDef>
        {
            new() { Id = 1, Keywords = new() { new() { Id = 1, RuleId = 1, Keyword = "子宫肌瘤", KeywordLen = 4 } } }
        };
        var trie = new TrieMatcher();
        trie.Build(rules);

        var hits = trie.FindAll("发现子宫肌瘤占位");
        Assert.Single(hits);
        Assert.Equal("子宫肌瘤", hits[0].Keyword);
    }

    [Fact]
    public void 长词优先_不重复匹配()
    {
        var rules = new List<RuleDef>
        {
            new()
            {
                Id = 1,
                Keywords = new()
                {
                    new() { Id = 1, RuleId = 1, Keyword = "子宫", KeywordLen = 2 },
                    new() { Id = 2, RuleId = 1, Keyword = "子宫肌瘤", KeywordLen = 4 },
                }
            }
        };
        var trie = new TrieMatcher();
        trie.Build(rules);

        var hits = trie.FindAll("子宫肌瘤");
        Assert.Single(hits);
        Assert.Equal("子宫肌瘤", hits[0].Keyword);
    }

    [Fact]
    public void 无匹配()
    {
        var rules = new List<RuleDef>
        {
            new() { Id = 1, Keywords = new() { new() { Id = 1, RuleId = 1, Keyword = "子宫肌瘤", KeywordLen = 4 } } }
        };
        var trie = new TrieMatcher();
        trie.Build(rules);

        var hits = trie.FindAll("双肺清晰");
        Assert.Empty(hits);
    }

    [Fact]
    public void 排除模式关键词不进Trie()
    {
        var rules = new List<RuleDef>
        {
            new()
            {
                Id = 1,
                Keywords = new()
                {
                    new() { Id = 1, RuleId = 1, Keyword = "切除术后", KeywordLen = 4, IsExclude = true },
                    new() { Id = 2, RuleId = 1, Keyword = "子宫肌瘤", KeywordLen = 4 },
                }
            }
        };
        var trie = new TrieMatcher();
        trie.Build(rules);

        var hits = trie.FindAll("切除术后子宫肌瘤");
        Assert.Single(hits);
        Assert.Equal("子宫肌瘤", hits[0].Keyword);
    }
}
```

- [ ] **Step 2: 编写 RuleEngine 集成测试（覆盖所有规则类型）**

```csharp
using Xunit;
using Agent_QC.Models;
using Agent_QC.Services;

namespace Agent_QC.Tests.UnitTests.Services;

public class RuleEngineTests
{
    // keyword_negation: GenderConflict
    [Fact]
    public void 男性患者出现子宫_报critical()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { PatientGender = "男", Findings = "子宫肌瘤" });
        Assert.Contains(result, i => i.IssueType == "gender_conflict" && i.Severity == "critical");
    }

    [Fact]
    public void 男性患者_未见子宫_不报错()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { PatientGender = "男", Findings = "未见子宫及附件异常" });
        Assert.DoesNotContain(result, i => i.IssueType == "gender_conflict");
    }

    [Fact]
    public void 女性患者出现前列腺_报critical()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { PatientGender = "女", Findings = "前列腺增生" });
        Assert.Contains(result, i => i.IssueType == "gender_conflict");
    }

    [Fact]
    public void 术后排除模式_不报错()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { PatientGender = "男", Findings = "子宫切除术后复查" });
        Assert.DoesNotContain(result, i => i.IssueType == "gender_conflict");
    }

    [Fact]
    public void 无性别信息_跳过检查()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { PatientGender = null, Findings = "子宫肌瘤" });
        Assert.DoesNotContain(result, i => i.IssueType == "gender_conflict");
    }

    // keyword_negation: CriticalSign
    [Fact]
    public void 危急征象_脑出血_报critical()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { Findings = "脑出血" });
        Assert.Contains(result, i => i.IssueType == "critical_sign" && i.Severity == "critical");
    }

    [Fact]
    public void 否定语境_未见脑出血_不报错()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { Findings = "未见明确脑出血" });
        Assert.DoesNotContain(result, i => i.IssueType == "critical_sign");
    }

    // keyword_age
    [Fact]
    public void 十岁出现骨质疏松_报error()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { PatientAge = 10, Findings = "骨质疏松" });
        Assert.Contains(result, i => i.IssueType == "age_conflict");
    }

    [Fact]
    public void 七十岁骨质疏松_不报错()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { PatientAge = 70, Findings = "骨质疏松" });
        Assert.DoesNotContain(result, i => i.IssueType == "age_conflict");
    }

    [Fact]
    public void 无年龄信息_跳过检查()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { PatientAge = null, Findings = "骨质疏松" });
        Assert.DoesNotContain(result, i => i.IssueType == "age_conflict");
    }

    // keyword_device
    [Fact]
    public void CT检查出现MRI示_报error()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { ExamDevice = "CT", Findings = "MRI示异常信号" });
        Assert.Contains(result, i => i.IssueType == "device_conflict" && i.Severity == "error");
    }

    // keyword_scan
    [Fact]
    public void 平扫出现增强描述_报error()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { ExamMethod = "平扫", Findings = "病灶明显强化" });
        Assert.Contains(result, i => i.IssueType == "scan_enhance_conflict");
    }

    [Fact]
    public void 增强检查出现增强描述_不报错()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { ExamMethod = "增强", Findings = "病灶明显强化" });
        Assert.DoesNotContain(result, i => i.IssueType == "scan_enhance_conflict");
    }

    // keyword_replace: phrase_typo
    [Fact]
    public void 错别字_低密谋灶_报error()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { Findings = "右肺低密谋灶" });
        Assert.Contains(result, i => i.SubType == "phrase_typo" && i.OriginalText == "低密谋灶");
    }

    // direction_compare
    [Fact]
    public void 所见左侧_结论右侧_报warning()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { Findings = "左侧结节", Impression = "右侧占位" });
        Assert.Contains(result, i => i.IssueType == "direction_conflict");
    }

    // cross_section: FindingsImpressionConsistency
    [Fact]
    public void 所见阴性_结论阳性_报error()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest
        {
            Findings = "双肺未见异常",
            Impression = "右肺结节，考虑恶性"
        });
        Assert.Contains(result, i => i.SubType == "findings_impression_consistency");
    }

    // regex_duplicate
    [Fact]
    public void 重复字_报warning()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { Findings = "检查查所见" });
        Assert.Contains(result, i => i.SubType == "duplicate_char" && i.OriginalText == "查查");
    }

    // field_validation
    [Fact]
    public void 性别无效_报error()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { PatientGender = "未知", ExamPart = "胸部", ExamDevice = "CT" });
        Assert.Contains(result, i => i.SubType == "invalid_field");
    }

    // 辅助方法
    private static RuleEngine CreateEngine()
    {
        var dbPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "knowledge", "rules.db");
        // 如果 rules.db 已存在（Task 6 产物），直接使用
        if (File.Exists(dbPath))
        {
            var engine = new RuleEngine(dbPath);
            engine.Initialize();
            return engine;
        }
        // fallback: 使用程序集中的临时路径
        throw new FileNotFoundException($"Test requires rules.db at {dbPath}. Run SeedRules first.");
    }
}
```

- [ ] **Step 3: 删除废弃的规则类和测试文件**

```bash
# Level 3 rules
rm src/Agent_QC/src/Services/Rules/GenderConflictRule.cs
rm src/Agent_QC/src/Services/Rules/AgeConflictRule.cs
rm src/Agent_QC/src/Services/Rules/DirectionConflictRule.cs
rm src/Agent_QC/src/Services/Rules/DeviceConflictRule.cs
rm src/Agent_QC/src/Services/Rules/ScanEnhanceConflictRule.cs
rm src/Agent_QC/src/Services/Rules/CriticalSignRule.cs

# Level 1 rules (except UnitFormatRule)
rm src/Agent_QC/src/Services/Rules/Level1/PhraseTypoRule.cs
rm src/Agent_QC/src/Services/Rules/Level1/DuplicateCharRule.cs
rm src/Agent_QC/src/Services/Rules/Level1/SentencePunctuationRule.cs
rm src/Agent_QC/src/Services/Rules/Level1/PatientInfoRule.cs
rm src/Agent_QC/src/Services/Rules/Level1/TerminologyStandardRule.cs

# Level 2 rules (all)
rm src/Agent_QC/src/Services/Rules/Level2/ColloquialTermRule.cs
rm src/Agent_QC/src/Services/Rules/Level2/AnatomyTermRule.cs
rm src/Agent_QC/src/Services/Rules/Level2/LesionCompletenessRule.cs
rm src/Agent_QC/src/Services/Rules/Level2/RadsClassificationRule.cs
rm src/Agent_QC/src/Services/Rules/Level2/FindingsImpressionConsistencyRule.cs
rm src/Agent_QC/src/Services/Rules/Level2/ComparisonDescriptionRule.cs
rm src/Agent_QC/src/Services/Rules/Level2/AdviceConsistencyRule.cs

# 删除对应测试
rm src/Agent_QC/tests/UnitTests/Services/Rules/GenderConflictRuleTests.cs
rm src/Agent_QC/tests/UnitTests/Services/Rules/AgeConflictRuleTests.cs
rm src/Agent_QC/tests/UnitTests/Services/Rules/DirectionConflictRuleTests.cs
rm src/Agent_QC/tests/UnitTests/Services/Rules/DeviceConflictRuleTests.cs
rm src/Agent_QC/tests/UnitTests/Services/Rules/ScanEnhanceConflictRuleTests.cs
rm src/Agent_QC/tests/UnitTests/Services/Rules/CriticalSignRuleTests.cs
rm -r src/Agent_QC/tests/UnitTests/Services/Rules/Level1
rm -r src/Agent_QC/tests/UnitTests/Services/Rules/Level2
```

> ⚠️ Level1 目录下保留 `UnitFormatRule.cs` 和 `UnitFormatRuleTests.cs`，删除前确认。

- [ ] **Step 4: 编译并运行测试**

```bash
dotnet build src/Agent_QC/ --configuration Release
dotnet test src/Agent_QC/tests/ --configuration Release
```

- [ ] **Step 5: 提交删除 + 新测试**

```bash
git add -A && git commit -m "feat(rule-engine): migrate 18 rules to SQLite+Trie RuleEngine

Replace hardcoded rule classes with unified RuleEngine backed by SQLite.
- Add RuleEngine, SqliteRuleStore, TrieMatcher, RuleExecutor
- Add RuleDef model
- Integrate into QcService and Program.cs
- Keep UnitFormatRule unchanged
- Delete 18 obsolete rule classes and their tests
- Add RuleEngineTests and TrieMatcherTests

Resolves #6"
```

---

### Task 9: 最终验证

- [ ] **Step 1: 运行完整测试套件**

```bash
dotnet test src/Agent_QC/tests/ --configuration Release
```

- [ ] **Step 2: 检查测试覆盖率**

```bash
dotnet test src/Agent_QC/tests/ --configuration Release --collect:"XPlat Code Coverage"
```

- [ ] **Step 3: 启动服务验证**

```bash
dotnet run --project src/Agent_QC/src/ --configuration Release
```

---

## 实现提示

1. **Task 6 (SeedRules) 必须在 Task 1-5 之后执行**，因为需要编译成功的项目才能运行。但实际上数据库中只有字符串数据，不依赖项目编译。所以可以先用独立 console project（与项目隔离）来运行迁移脚本。

2. **RuleExecutor 中的 `keywordById` 映射**：在 Task 4 中所有需要访问 `ExtraData` 的 handler（keyword_age, keyword_replace）都通过 `keywordById` 字典获取。

3. **cross_section 规则需要仔细映射**：每个原有规则类的条件逻辑在 params_json 中表达。如 `LesionCompletenessRule` 的"有病灶→须有尺寸"和"多发→须有数量"是两条独立的 rule_def 行。

4. **文件删除前确认 `UnitFormatRule.cs` 和 `UnitFormatRuleTests.cs` 保留**。
