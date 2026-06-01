using Xunit;
using Agent_QC.Models;
using Agent_QC.Services;

namespace Agent_QC.Tests.UnitTests.Services;

public class EntityNormalizerTests
{
    private static EntityNormalizer CreateNormalizer()
    {
        // Use the real terminology.yaml from the project knowledge directory
        var baseDir = AppContext.BaseDirectory;
        var path = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "..", "knowledge", "terminology.yaml"));
        if (!File.Exists(path))
            path = Path.Combine(baseDir, "knowledge", "terminology.yaml");
        return new EntityNormalizer(path);
    }

    [Fact]
    public void Normalize_EmptyList_ReturnsEmpty()
    {
        var n = CreateNormalizer();
        var result = n.Normalize(new List<NerEntity>());
        Assert.Empty(result);
    }

    [Fact]
    public void Normalize_NullList_ReturnsEmpty()
    {
        var n = CreateNormalizer();
        var result = n.Normalize(null!);
        Assert.Empty(result);
    }

    [Fact]
    public void Normalize_SynonymMapsToCanonical()
    {
        var n = CreateNormalizer();
        var entities = new List<NerEntity>
        {
            new() { Type = "anatomy", Text = "右上肺", Start = 0, End = 3, Confidence = 0.9f },
        };
        var result = n.Normalize(entities);
        Assert.Single(result);
        Assert.Equal("右肺上叶", result[0].Normalized);
    }

    [Fact]
    public void Normalize_UnknownTerm_KeepsOriginal()
    {
        var n = CreateNormalizer();
        var entities = new List<NerEntity>
        {
            new() { Type = "anatomy", Text = "未知部位XYZ", Start = 0, End = 6, Confidence = 0.9f },
        };
        var result = n.Normalize(entities);
        Assert.Single(result);
        Assert.Equal("未知部位XYZ", result[0].Normalized);
    }

    [Fact]
    public void Normalize_StandardTerm_StaysUnchanged()
    {
        var n = CreateNormalizer();
        var entities = new List<NerEntity>
        {
            new() { Type = "anatomy", Text = "前列腺", Start = 0, End = 3, Confidence = 0.9f },
        };
        var result = n.Normalize(entities);
        Assert.Single(result);
        Assert.Equal("前列腺", result[0].Normalized);
    }

    [Fact]
    public void Deduplicate_OverlappingSpans_KeepsLongest()
    {
        var n = CreateNormalizer();
        var entities = new List<NerEntity>
        {
            new() { Type = "anatomy", Text = "右肺", Start = 0, End = 2, Confidence = 0.9f },
            new() { Type = "anatomy", Text = "右肺上叶", Start = 0, End = 4, Confidence = 0.9f },
        };
        var result = n.Normalize(entities);
        Assert.Single(result);
        Assert.Equal("右肺上叶", result[0].Normalized);
    }

    [Fact]
    public void Deduplicate_Overlapping_HigherConfidenceWins()
    {
        var n = CreateNormalizer();
        var entities = new List<NerEntity>
        {
            new() { Type = "anatomy", Text = "短词", Start = 0, End = 2, Confidence = 0.9f },
            new() { Type = "anatomy", Text = "长词版本", Start = 0, End = 4, Confidence = 0.5f },
        };
        var result = n.Normalize(entities);
        Assert.Single(result);
        Assert.Equal("短词", result[0].Normalized);
    }

    [Fact]
    public void Deduplicate_NonOverlapping_KeptAll()
    {
        var n = CreateNormalizer();
        var entities = new List<NerEntity>
        {
            new() { Type = "anatomy", Text = "肺部", Start = 0, End = 2, Confidence = 0.9f },
            new() { Type = "anatomy", Text = "肝脏", Start = 5, End = 7, Confidence = 0.9f },
        };
        var result = n.Normalize(entities);
        Assert.Equal(2, result.Count);
    }
}
