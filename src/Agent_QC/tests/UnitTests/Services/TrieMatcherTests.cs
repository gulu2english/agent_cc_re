using Xunit;
using Agent_QC.Models;
using Agent_QC.Services;

namespace Agent_QC.Tests.UnitTests.Services;

public class TrieMatcherTests
{
    [Fact]
    public void SingleKeywordMatch()
    {
        var rules = new List<RuleDef>
        {
            new()
            {
                Id = 1,
                Keywords = new()
                {
                    new RuleKeyword { Id = 1, RuleId = 1, Keyword = "脑出血", KeywordLen = 3 }
                }
            }
        };
        var trie = new TrieMatcher();
        trie.Build(rules);

        var hits = trie.FindAll("发现脑出血占位");
        Assert.Single(hits);
        Assert.Equal("脑出血", hits[0].Keyword);
    }

    [Fact]
    public void LongestMatchPriority_NoDuplicateMatch()
    {
        var rules = new List<RuleDef>
        {
            new()
            {
                Id = 1,
                Keywords = new()
                {
                    new RuleKeyword { Id = 1, RuleId = 1, Keyword = "子宫", KeywordLen = 2 },
                    new RuleKeyword { Id = 2, RuleId = 1, Keyword = "子宫肌瘤", KeywordLen = 4 },
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
    public void NoMatch()
    {
        var rules = new List<RuleDef>
        {
            new()
            {
                Id = 1,
                Keywords = new()
                {
                    new RuleKeyword { Id = 1, RuleId = 1, Keyword = "子宫肌瘤", KeywordLen = 4 }
                }
            }
        };
        var trie = new TrieMatcher();
        trie.Build(rules);

        var hits = trie.FindAll("双肺清晰");
        Assert.Empty(hits);
    }

    [Fact]
    public void ExcludeKeywordsNotInTrie()
    {
        var rules = new List<RuleDef>
        {
            new()
            {
                Id = 1,
                Keywords = new()
                {
                    new RuleKeyword { Id = 1, RuleId = 1, Keyword = "切除术后", KeywordLen = 4, IsExclude = true },
                    new RuleKeyword { Id = 2, RuleId = 1, Keyword = "子宫肌瘤", KeywordLen = 4 },
                }
            }
        };
        var trie = new TrieMatcher();
        trie.Build(rules);

        var hits = trie.FindAll("切除术后子宫肌瘤");
        Assert.Single(hits);
        Assert.Equal("子宫肌瘤", hits[0].Keyword);
    }

    [Fact]
    public void MultipleMatchesInText()
    {
        var rules = new List<RuleDef>
        {
            new()
            {
                Id = 1,
                Keywords = new()
                {
                    new RuleKeyword { Id = 1, RuleId = 1, Keyword = "脑出血", KeywordLen = 3 },
                    new RuleKeyword { Id = 2, RuleId = 1, Keyword = "骨折", KeywordLen = 2 },
                }
            }
        };
        var trie = new TrieMatcher();
        trie.Build(rules);

        var hits = trie.FindAll("脑出血合并骨折");
        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Keyword == "脑出血");
        Assert.Contains(hits, h => h.Keyword == "骨折");
    }
}
