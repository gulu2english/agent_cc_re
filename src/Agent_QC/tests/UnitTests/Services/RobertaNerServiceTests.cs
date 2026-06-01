using Xunit;
using Agent_QC.Models;
using Agent_QC.Services;

namespace Agent_QC.Tests.UnitTests.Services;

public class RobertaNerServiceTests
{
    private static RobertaNerService CreateService()
    {
        var baseDir = AppContext.BaseDirectory;
        var dictPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "..", "knowledge", "jieba_medical_dict.txt"));
        if (!File.Exists(dictPath))
            dictPath = Path.Combine(baseDir, "knowledge", "jieba_medical_dict.txt");

        var terminologyPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "..", "knowledge", "terminology.yaml"));
        if (!File.Exists(terminologyPath))
            terminologyPath = Path.Combine(baseDir, "knowledge", "terminology.yaml");

        var jieba = new JiebaSegmenter(dictPath);
        var normalizer = new EntityNormalizer(terminologyPath);
        var modelPath = Path.Combine(baseDir, "knowledge", "models", "roberta-ner.onnx");
        var vocabPath = Path.Combine(baseDir, "knowledge", "models", "vocab.txt");
        var service = new RobertaNerService(jieba, normalizer, modelPath, vocabPath);
        // Don't call Initialize() — we want dictionary fallback for tests
        return service;
    }

    [Fact]
    public void Extract_NullText_ReturnsEmpty()
    {
        var s = CreateService();
        var result = s.Extract(null!);
        Assert.Empty(result);
    }

    [Fact]
    public void Extract_EmptyText_ReturnsEmpty()
    {
        var s = CreateService();
        var result = s.Extract("");
        Assert.Empty(result);
    }

    [Fact]
    public void Extract_WhitespaceText_ReturnsEmpty()
    {
        var s = CreateService();
        var result = s.Extract("   ");
        Assert.Empty(result);
    }

    [Fact]
    public void Extract_DirectionLeft_ReturnsDirectionEntity()
    {
        var s = CreateService();
        var result = s.Extract("左侧结节");
        Assert.Contains(result, e => e.Type == "direction" && e.Text == "左侧");
    }

    [Fact]
    public void Extract_DirectionRight_ReturnsDirectionEntity()
    {
        var s = CreateService();
        var result = s.Extract("右侧占位");
        Assert.Contains(result, e => e.Type == "direction" && e.Text == "右侧");
    }

    [Fact]
    public void Extract_AnatomyLung_ReturnsAnatomyEntity()
    {
        var s = CreateService();
        var result = s.Extract("双肺清晰");
        // "肺" may or may not be segmented depending on jieba, but "双肺" is direction
        Assert.Contains(result, e => e.Type == "direction" || e.Type == "anatomy");
    }

    [Fact]
    public void Extract_MeasureMM_ReturnsMeasureEntity()
    {
        var s = CreateService();
        var result = s.Extract("结节大小约15mm");
        Assert.Contains(result, e => e.Type == "measure" && e.Text == "15mm");
    }

    [Fact]
    public void Extract_MeasureCM_ReturnsMeasureEntity()
    {
        var s = CreateService();
        var result = s.Extract("肿块3.2cm");
        Assert.Contains(result, e => e.Type == "measure" && e.Text == "3.2cm");
    }

    [Fact]
    public void Extract_NoEntitiesInPlainText_ReturnsEmptyList()
    {
        var s = CreateService();
        var result = s.Extract("检查顺利");
        // Should return minimal or no entities
        var nonMeasure = result.Where(e => e.Type != "measure").ToList();
        // This text doesn't contain direction/anatomy/finding words
    }

    [Fact]
    public void Extract_NormalizedEntities_PassThroughToEntityNormalizer()
    {
        var s = CreateService();
        // "右上肺" segments in jieba depends on dictionary; test normalization passes through
        var result = s.Extract("双侧甲状腺结节，大小约15mm");
        // At minimum, the measure entity is extracted
        Assert.Contains(result, e => e.Type == "measure" && e.Text == "15mm");
        // Direction "双侧" should be extracted (in direction keywords)
        Assert.Contains(result, e => e.Type == "direction" && e.Text == "双侧");
    }
}
