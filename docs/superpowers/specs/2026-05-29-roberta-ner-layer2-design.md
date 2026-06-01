# RoBERTa NER + Logic Engine: Level 2 Pipeline Integration

## Goal

Add a Level 2 defense layer to the QC pipeline: RoBERTa NER entity extraction (in-process ONNX GPU) followed by a deterministic LINQ logic engine for structured cross-field comparison. Resolves Issue #5.

## Architecture

```
QcRequest → Level 0: JiebaSegmenter (CPU, ~5ms)
         → Level 1: RuleEngine (CPU, <10ms)
         → Level 2: RobertaNerService + LogicEngine (GPU, <50ms)  ← NEW
         → Level 3: Hermes Skill Squad via vLLM (GPU, ~120ms)
         → Level 4: QA Arbiter + Scoring (CPU, ~20ms)
         → QcResponse
```

Level 2 internal flow:

```
RobertaNerService (ONNX inference)
    ├── Load RoBERTa ONNX FP16 model at startup
    ├── Tokenize → run inference → decode entity spans
    └── Fallback: DictionaryMatcher if GPU unhealthy

EntityNormalizer
    ├── Map entity synonyms → canonical forms (terminology.yaml)
    └── Deduplicate overlapping spans

LogicEngine (LINQ)
    ├── DirectionConflict: left/right entities in findings vs impression
    ├── GenderAnatomyConflict: gender ↔ anatomy entity cross-ref
    ├── SiteOmission: findings anatomy entities must appear in impression
    └── ExamConsistency: exam entities vs ExamDevice/ExamMethod fields
```

## Model

- **Base:** RoBERTa-wwm-ext (base, 110M params), fine-tuned on CMeEE v2
- **Format:** ONNX FP16 (~350MB VRAM)
- **Inference:** `Microsoft.ML.OnnxRuntime.Gpu` — same stack as MedBERT
- **Input:** Raw text, max 512 tokens per pass. Longer text split by sentence boundary.
- **Output:** `List<NerEntity>` with type, text, span positions, confidence

### Entity Types (9 CM → 4 internal groups)

| Internal Type | CM Types | Used By |
|---------------|----------|---------|
| `anatomy` | 部位(body) | site-omission, gender-anatomy |
| `direction` | 方位(loc) | direction-conflict |
| `finding` | 疾病(dis), 症状(sym), 检查(exam), 治疗(treat), 药物(drug), 阴阳性极性 | findings-impression-nli |
| `measure` | 数值(val) + units | measurement-completeness |

### Health Check + Fallback

```
Startup → RobertaNerService.Initialize()
    ├── GPU healthy + model found → load ONNX, Mode = Gpu
    ├── GPU healthy + model missing → log warning, Mode = Dictionary
    └── GPU unhealthy → Mode = Dictionary

Dictionary fallback:
    anatomy/direction → terminology.yaml synonym lists + jieba POS tags
    finding → knowledge-base.yaml term lists
    measure → regex \d+\.?\d*\s*(mm|cm|ml|HU)
```

## Entity Model

```csharp
public record NerEntity
{
    public string Type { get; init; }      // "anatomy" | "direction" | "finding" | "measure"
    public string Text { get; init; }
    public string Normalized { get; init; }
    public int Start { get; init; }
    public int End { get; init; }
    public float Confidence { get; init; }
}
```

## LogicEngine Comparators

Four deterministic LINQ methods. Each receives findings entities, impression entities, and the request. Returns `List<QcIssueDto>`.

### DirectionConflict

Extract direction entities (左/右/双侧). If findings contain exclusively left-side entities but impression contains right-side entities → issue. Vice versa. Handles bilateral (双侧) as neutral.

### GenderAnatomyConflict

Cross-reference `PatientGender` against anatomy entities. Male + female anatomy (子宫, 卵巢, 附件, 乳腺...) → issue. Female + male anatomy (前列腺, 睾丸, 精囊...) → issue. Complements RuleEngine keyword_negation — entity-level detection catches names not in the keyword list.

### SiteOmission

Every `anatomy` entity in findings must appear in impression, unless the findings sentence contains negation (未见, 无明显, 未显示...). If a site appears in findings without a corresponding impression mention → issue.

### ExamConsistency

Cross-check `exam` entities against `ExamDevice`/`ExamMethod`. "MRI示" entity with ExamDevice="CT" → issue. Complements RuleEngine keyword_device.

### Deduplication with Level 1

LogicEngine receives existing Level 1 issues. If a RuleEngine issue already covers the same finding (same keyword + same location), LogicEngine skips the duplicate.

## QcService Integration

```csharp
// Level 0
request.SegmentedFindings = _jieba.Segment(request.Findings ?? "");
request.SegmentedImpression = _jieba.Segment(request.Impression ?? "");

// Level 1: RuleEngine (existing)
issues.AddRange(_ruleEngine.Execute(request));

// Level 2: RoBERTa NER + Logic Engine (NEW)
var findingsEntities = _robertaNer.Extract(request.Findings ?? "");
var impressionEntities = _robertaNer.Extract(request.Impression ?? "");
var nFindings = _entityNormalizer.Normalize(findingsEntities);
var nImpression = _entityNormalizer.Normalize(impressionEntities);
issues.AddRange(_logicEngine.Compare(request, nFindings, nImpression, issues));

// Levels 3-4: unchanged
```

## File Manifest

### New

| File | Purpose |
|------|---------|
| `src/Services/RobertaNerService.cs` | ONNX load, tokenize, inference, Extract() |
| `src/Services/EntityNormalizer.cs` | Synonym map, span dedup, normalize |
| `src/Services/LogicEngine.cs` | 4 comparators, dedup with Level 1 |
| `src/Models/NerEntity.cs` | Entity record type |
| `knowledge/models/roberta-ner.onnx` | ONNX model (~200MB, gitignored) |
| `knowledge/models/roberta-ner-tokenizer.json` | Vocab/config for BERT tokenizer |
| `scripts/ConvertRoBERTa/convert.py` | One-time PyTorch→ONNX conversion |
| `tests/UnitTests/Services/RobertaNerServiceTests.cs` | Unit tests with mock inference |
| `tests/UnitTests/Services/EntityNormalizerTests.cs` | Synonym mapping tests |
| `tests/UnitTests/Services/LogicEngineTests.cs` | Comparator logic tests |
| `tests/IntegrationTests/RobertaNerIntegrationTests.cs` | GPU integration (Category=GPU) |

### Modified

| File | Change |
|------|--------|
| `src/Services/QcService.cs` | Add Level 2 block after RuleEngine |
| `src/Program.cs` | Register RobertaNerService, EntityNormalizer, LogicEngine as singletons |
| `src/Agent_QC.csproj` | Add `Microsoft.ML.OnnxRuntime.Gpu` package (already needed for MedBERT) |

## Constraints

- VRAM budget: RoBERTa ~0.35GB + MedBERT ~0.2GB + vLLM ~5.0GB = ~5.55GB / 7.7GB
- Level 2 latency target: <50ms total (inference + comparison)
- Fallback must NOT crash pipeline — degrade gracefully to dictionary mode
- LogicEngine tests do NOT require GPU — mock the entity inputs
- Entity types extensible: adding a new type = new enum value + comparator method
- Model file gitignored (too large); tracked via `knowledge/models/README.md` with download link

## Not In Scope

- Model training or fine-tuning (assumes pre-trained ONNX model available)
- Replacing Level 1 RuleEngine rules — Level 2 complements, not replaces
- Entity relationship extraction (relation triplets)
- Multi-turn or conversational NER
