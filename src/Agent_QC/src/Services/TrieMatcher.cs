using Agent_QC.Models;

namespace Agent_QC.Services;

public class TrieNode
{
    public Dictionary<char, TrieNode> Children { get; } = new();
    public List<int> KeywordIds { get; } = new();
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
            .Where(k => k.IsExclude == false)
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
                    lastMatchEnd = j;
                    lastMatchKwId = node.KeywordIds[0];
                }
            }

            if (lastMatchEnd > i)
            {
                var kw = _keywordById[lastMatchKwId];
                hits.Add(new TrieHit(kw.Keyword, i, lastMatchKwId, kw.RuleId));
                i = lastMatchEnd;
            }
            else
            {
                i++;
            }
        }

        return hits;
    }

    public bool ContainsAny(string text, IEnumerable<string> terms)
    {
        return terms.Any(term =>
            FindAll(text).Any(h => h.Keyword == term));
    }
}

public record TrieHit(string Keyword, int StartPos, int KeywordId, int RuleId);
