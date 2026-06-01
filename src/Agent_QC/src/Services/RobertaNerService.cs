using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Agent_QC.Models;

namespace Agent_QC.Services;

/// <summary>NER entity extraction service. Primary: ONNX RoBERTa inference. Fallback: dictionary + jieba + regex.</summary>
public partial class RobertaNerService : IDisposable
{
    private readonly JiebaSegmenter _jieba;
    private readonly EntityNormalizer _normalizer;
    private readonly string _modelPath;
    private readonly string _vocabPath;
    private readonly HashSet<string> _anatomyTerms = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directionTerms = new(StringComparer.Ordinal);
    private readonly List<string> _findingPatterns = new();

    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;
    private NerMode _mode = NerMode.Dictionary;

    // CMeEE label set (19 labels) — maps label ID to (entity_type, is_begin)
    private static readonly (string Type, bool IsBegin)[] LabelMap = new (string, bool)[19]
    {
        ("O", false),           // 0
        ("finding", true),      // 1  B-dis
        ("finding", false),     // 2  I-dis
        ("finding", true),      // 3  B-sym
        ("finding", false),     // 4  I-sym
        ("finding", true),      // 5  B-dru
        ("finding", false),     // 6  I-dru
        ("finding", true),      // 7  B-pro
        ("finding", false),     // 8  I-pro
        ("finding", true),      // 9  B-equ
        ("finding", false),     // 10 I-equ
        ("finding", true),      // 11 B-ite
        ("finding", false),     // 12 I-ite
        ("anatomy", true),      // 13 B-bod
        ("anatomy", false),     // 14 I-bod
        ("finding", true),      // 15 B-dep
        ("finding", false),     // 16 I-dep
        ("finding", true),      // 17 B-mic
        ("finding", false),     // 18 I-mic
    };

    private enum NerMode { Onnx, Dictionary }

    // Direction words for dictionary fallback
    private static readonly string[] DirectionKeywords =
    {
        "左侧", "右侧", "左", "右", "双侧", "双叶", "双肺", "双肾", "两边", "左右",
        "左上", "左下", "右上", "右下", "左前", "右前", "左后", "右后",
        "左叶", "右叶", "左半", "右半", "左侧壁", "右侧壁",
    };

    [GeneratedRegex(@"(\d+\.?\d*)\s*(mm|cm|ml|dl|l|mm²|cm²|mm3|cm3|HU|g|kg|mg|μg|%|℃|°|度|时|分|秒|天|周|月|年)", RegexOptions.Compiled)]
    private static partial Regex MeasurePattern();

    public RobertaNerService(JiebaSegmenter jieba, EntityNormalizer normalizer,
        string modelPath, string vocabPath)
    {
        _jieba = jieba;
        _normalizer = normalizer;
        _modelPath = modelPath;
        _vocabPath = vocabPath;
        BuildDictionaryIndexes();
    }

    /// <summary>Initialize ONNX model if available. Call once at startup.</summary>
    public void Initialize()
    {
        if (File.Exists(_modelPath) && File.Exists(_vocabPath))
        {
            try
            {
                _tokenizer = new BertTokenizer(_vocabPath, maxLength: 256);

                var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
                try
                {
                    sessionOptions.AppendExecutionProvider_CUDA();
                    Console.WriteLine("[RobertaNer] CUDA execution provider enabled");
                }
                catch
                {
                    Console.WriteLine("[RobertaNer] CUDA not available, using CPU");
                }

                _session = new InferenceSession(_modelPath, sessionOptions);
                _mode = NerMode.Onnx;
                Console.WriteLine("[RobertaNer] ONNX model loaded successfully");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RobertaNer] Failed to load ONNX model: {ex.Message}. Using dictionary fallback.");
                _mode = NerMode.Dictionary;
            }
        }
        else
        {
            Console.WriteLine($"[RobertaNer] Model files not found ({_modelPath}, {_vocabPath}), using dictionary fallback.");
            _mode = NerMode.Dictionary;
        }
    }

    /// <summary>Extract entities from text. Returns empty list for null/empty input.</summary>
    public List<NerEntity> Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<NerEntity>();

        return _mode switch
        {
            NerMode.Onnx => ExtractOnnx(text),
            _ => ExtractDictionary(text),
        };
    }

    // ── ONNX inference path ────────────────────────────

    private List<NerEntity> ExtractOnnx(string text)
    {
        if (_session == null || _tokenizer == null)
            return ExtractDictionary(text);

        var (inputIds, attentionMask, _) = _tokenizer.Tokenize(text);
        var seqLen = inputIds.Length;

        // Create tensors [1, seqLen]
        var inputTensor = new DenseTensor<long>(inputIds, new[] { 1, seqLen });
        var maskTensor = new DenseTensor<long>(attentionMask, new[] { 1, seqLen });
        var typeIdsTensor = new DenseTensor<long>(new long[seqLen], new[] { 1, seqLen });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", typeIdsTensor),
        };

        using var results = _session.Run(inputs);
        var logits = results.First().AsTensor<float>(); // [1, seq_len, 19]

        return DecodeBioTags(text, inputIds, logits, seqLen);
    }

    private List<NerEntity> DecodeBioTags(string text, long[] inputIds, Tensor<float> logits, int seqLen)
    {
        var entities = new List<NerEntity>();

        if (_tokenizer == null) return entities;

        // Token position → original char position mapping
        var tokenPositions = BuildTokenCharMap(text, inputIds, seqLen);

        int currentEntityStart = -1;
        int currentEntityCharStart = -1;
        string currentType = "";
        float currentConfSum = 0;
        int currentConfCount = 0;
        var currentTokens = new List<string>();

        for (int i = 0; i < seqLen; i++)
        {
            // Compute softmax and get best label
            int bestLabel = 0;
            float bestScore = 0;
            float maxLogit = float.MinValue;
            float sumExp = 0;

            // Find max logit for this token
            for (int l = 0; l < 19; l++)
            {
                float val = logits[0, i, l];
                if (val > maxLogit) maxLogit = val;
            }
            // Softmax with max subtraction for numerical stability
            var probs = new float[19];
            for (int l = 0; l < 19; l++)
            {
                probs[l] = MathF.Exp(logits[0, i, l] - maxLogit);
                sumExp += probs[l];
            }
            for (int l = 0; l < 19; l++)
            {
                probs[l] /= sumExp;
                if (probs[l] > bestScore)
                {
                    bestScore = probs[l];
                    bestLabel = l;
                }
            }

            var (entityType, isBegin) = LabelMap[bestLabel];

            if (entityType == "O" || isBegin)
            {
                // Flush previous entity
                if (currentEntityStart >= 0)
                {
                    var avgConf = currentConfCount > 0 ? currentConfSum / currentConfCount : bestScore;
                    entities.Add(new NerEntity
                    {
                        Type = currentType,
                        Text = string.Join("", currentTokens),
                        Normalized = string.Join("", currentTokens),
                        Start = currentEntityCharStart,
                        End = currentEntityCharStart + string.Join("", currentTokens).Length,
                        Confidence = avgConf,
                    });
                    currentTokens.Clear();
                }

                if (entityType != "O" && isBegin)
                {
                    // Start new entity
                    currentEntityStart = i;
                    currentType = entityType;
                    currentConfSum = bestScore;
                    currentConfCount = 1;
                    if (tokenPositions.TryGetValue(i, out var charPos))
                        currentEntityCharStart = charPos;
                    else
                        currentEntityCharStart = 0;
                    currentTokens.Add(_tokenizer.IdToToken((int)inputIds[i]));
                }
                else
                {
                    currentEntityStart = -1;
                    currentType = "";
                    currentConfSum = 0;
                    currentConfCount = 0;
                }
            }
            else
            {
                // I- tag: continue entity
                if (entityType == currentType)
                {
                    currentConfSum += bestScore;
                    currentConfCount++;
                    currentTokens.Add(_tokenizer.IdToToken((int)inputIds[i]));
                }
            }
        }

        // Flush last entity
        if (currentEntityStart >= 0)
        {
            var avgConf = currentConfCount > 0 ? currentConfSum / currentConfCount : 0.8f;
            entities.Add(new NerEntity
            {
                Type = currentType,
                Text = string.Join("", currentTokens),
                Normalized = string.Join("", currentTokens),
                Start = currentEntityCharStart,
                End = currentEntityCharStart + string.Join("", currentTokens).Length,
                Confidence = avgConf,
            });
        }

        return _normalizer.Normalize(entities);
    }

    /// <summary>Map token positions to original character positions.</summary>
    private Dictionary<int, int> BuildTokenCharMap(string text, long[] inputIds, int seqLen)
    {
        var map = new Dictionary<int, int>();
        if (_tokenizer == null) return map;

        int charPos = 0;
        for (int i = 1; i < seqLen - 1; i++) // skip [CLS] and [SEP]
        {
            if (i >= inputIds.Length) break;
            var token = _tokenizer.IdToToken((int)inputIds[i]);
            if (token == "[SEP]" || token == "[PAD]") break;

            map[i] = charPos;

            // Advance char position
            if (token.StartsWith("##"))
                charPos += token.Length - 2;
            else
                charPos += token.Length;

            if (charPos >= text.Length) charPos = text.Length - 1;
        }
        return map;
    }

    // ── Dictionary fallback path ───────────────────────

    private List<NerEntity> ExtractDictionary(string text)
    {
        var tokens = _jieba.Segment(text);
        var entities = new List<NerEntity>();
        var idx = 0;

        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token)) { idx++; continue; }

            var start = text.IndexOf(token, idx, StringComparison.Ordinal);
            if (start < 0) start = idx;
            var end = start + token.Length;

            if (_directionTerms.Contains(token))
            {
                entities.Add(new NerEntity
                {
                    Type = "direction", Text = token, Normalized = token,
                    Start = start, End = end, Confidence = 0.95f,
                });
            }
            else if (_anatomyTerms.Contains(token))
            {
                entities.Add(new NerEntity
                {
                    Type = "anatomy", Text = token, Normalized = token,
                    Start = start, End = end, Confidence = 0.90f,
                });
            }
            else if (_findingPatterns.Any(p => token.Contains(p, StringComparison.Ordinal)))
            {
                entities.Add(new NerEntity
                {
                    Type = "finding", Text = token, Normalized = token,
                    Start = start, End = end, Confidence = 0.80f,
                });
            }

            idx = end;
        }

        foreach (Match m in MeasurePattern().Matches(text))
        {
            entities.Add(new NerEntity
            {
                Type = "measure", Text = m.Value, Normalized = m.Value,
                Start = m.Index, End = m.Index + m.Length, Confidence = 0.98f,
            });
        }

        return _normalizer.Normalize(entities);
    }

    // ── Dictionary index builders ──────────────────────

    private void BuildDictionaryIndexes()
    {
        var commonAnatomy = new[]
        {
            "肺", "肝", "肾", "脾", "胰", "胃", "肠", "胆", "脑", "心",
            "子宫", "卵巢", "前列腺", "甲状腺", "乳腺", "膀胱", "骨骼",
        };
        foreach (var t in commonAnatomy) _anatomyTerms.Add(t);

        foreach (var t in DirectionKeywords) _directionTerms.Add(t);

        var patterns = new[]
        {
            "结节", "肿块", "占位", "阴影", "密度", "钙化", "囊肿", "积液",
            "增厚", "狭窄", "扩张", "变形", "破坏", "侵蚀", "融合",
            "强化", "坏死", "出血", "水肿", "炎症", "纤维化", "硬化",
            "畸形", "移位", "脱出", "突出", "膨出", "骨折", "裂缝",
            "不张", "气肿", "积液", "结石", "梗阻", "穿孔", "破裂",
            "病灶", "病变", "异常", "改变", "信号", "影",
        };
        _findingPatterns.AddRange(patterns);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
