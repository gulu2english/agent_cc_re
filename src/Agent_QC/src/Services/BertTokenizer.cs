namespace Agent_QC.Services;

/// <summary>Lightweight BERT WordPiece tokenizer for Chinese RoBERTa.</summary>
public class BertTokenizer
{
    private readonly Dictionary<string, int> _vocab = new(StringComparer.Ordinal);
    private readonly string[] _idToToken;
    private readonly int _clsTokenId;
    private readonly int _sepTokenId;
    private readonly int _padTokenId;
    private readonly int _unkTokenId;
    private readonly int _maxLength;

    public int VocabSize { get; }
    public int MaxLength => _maxLength;
    public int ClsTokenId => _clsTokenId;
    public int SepTokenId => _sepTokenId;
    public int PadTokenId => _padTokenId;

    public BertTokenizer(string vocabPath, int maxLength = 256)
    {
        _maxLength = maxLength;

        if (!File.Exists(vocabPath))
            throw new FileNotFoundException($"vocab.txt not found: {vocabPath}");

        var lines = File.ReadAllLines(vocabPath);
        _idToToken = new string[lines.Length];
        VocabSize = lines.Length;

        for (int i = 0; i < lines.Length; i++)
        {
            var token = lines[i].Trim();
            _vocab[token] = i;
            _idToToken[i] = token;
        }

        _clsTokenId = _vocab.GetValueOrDefault("[CLS]", 101);
        _sepTokenId = _vocab.GetValueOrDefault("[SEP]", 102);
        _padTokenId = _vocab.GetValueOrDefault("[PAD]", 0);
        _unkTokenId = _vocab.GetValueOrDefault("[UNK]", 100);
    }

    /// <summary>Tokenize text and return input_ids, attention_mask, token_type_ids.</summary>
    public (long[] InputIds, long[] AttentionMask, long[] TokenTypeIds) Tokenize(string text)
    {
        // For Chinese RoBERTa: each Chinese char is its own token.
        // Non-Chinese sequences use WordPiece subword splitting.
        var tokens = new List<string> { "[CLS]" };

        int i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (IsCjk(c))
            {
                var tok = c.ToString();
                tokens.Add(_vocab.ContainsKey(tok) ? tok : "[UNK]");
                i++;
            }
            else if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else
            {
                // Non-CJK: WordPiece greedy longest match
                int end = i;
                while (end < text.Length && !IsCjk(text[end]) && !char.IsWhiteSpace(text[end]))
                    end++;
                var span = text[i..end];
                TokenizeWordPiece(span, tokens);
                i = end;
            }
        }

        tokens.Add("[SEP]");

        // Truncate to max_length
        if (tokens.Count > _maxLength)
            tokens = tokens.Take(_maxLength - 1).Append("[SEP]").ToList();

        var seqLen = tokens.Count;
        var inputIds = new long[seqLen];
        var attentionMask = new long[seqLen];
        var tokenTypeIds = new long[seqLen];

        for (int j = 0; j < seqLen; j++)
        {
            inputIds[j] = _vocab.GetValueOrDefault(tokens[j], _unkTokenId);
            attentionMask[j] = 1;
            tokenTypeIds[j] = 0;
        }

        return (inputIds, attentionMask, tokenTypeIds);
    }

    public string IdToToken(int id)
    {
        return id >= 0 && id < _idToToken.Length ? _idToToken[id] : "[UNK]";
    }

    // ── WordPiece subword ──────────────────────────────

    private void TokenizeWordPiece(string text, List<string> tokens)
    {
        int start = 0;
        while (start < text.Length)
        {
            int end = text.Length;
            string? found = null;

            while (end > start)
            {
                var sub = (start == 0 ? "" : "##") + text[start..end];
                if (_vocab.ContainsKey(sub))
                {
                    found = sub;
                    break;
                }
                end--;
            }

            if (found != null)
            {
                tokens.Add(found);
                start = end;
            }
            else
            {
                tokens.Add("[UNK]");
                start++;
            }
        }
    }

    private static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) ||
        (c >= 0x3400 && c <= 0x4DBF) ||
        (c >= 0x20000 && c <= 0x2A6DF) ||
        (c >= 0xF900 && c <= 0xFAFF);
}
