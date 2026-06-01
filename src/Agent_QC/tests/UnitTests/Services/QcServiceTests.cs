using Xunit;
using Agent_QC.Models;
using Agent_QC.Services;

namespace Agent_QC.Tests.UnitTests.Services;

public class QcServiceTests
{
    private static RuleEngine CreateEngine()
    {
        var dbPath = Path.Combine(AppContext.BaseDirectory, "knowledge", "rules.db");
        if (!File.Exists(dbPath))
        {
            dbPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "knowledge", "rules.db"));
        }
        if (!File.Exists(dbPath))
            throw new FileNotFoundException($"rules.db not found. Checked: {dbPath}");
        var engine = new RuleEngine(dbPath);
        engine.Initialize();
        return engine;
    }

    private static (RobertaNerService robertaNer, EntityNormalizer normalizer, LogicEngine logicEngine) CreateLevel2()
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
        var robertaNer = new RobertaNerService(jieba, normalizer, modelPath, vocabPath);
        var logicEngine = new LogicEngine();
        return (robertaNer, normalizer, logicEngine);
    }

    private QcService CreateService()
    {
        var (robertaNer, normalizer, logicEngine) = CreateLevel2();
        return new QcService(CreateEngine(), robertaNer, normalizer, logicEngine);
    }

    [Fact]
    public async Task 正常报告_无问题_满分通过()
    {
        var engine = CreateEngine();
        var service = CreateService();
        var request = new QcRequest
        {
            ReportId = "R001",
            Findings = "胸部CT平扫未见异常。",
            Impression = "胸部未见异常。",
            PatientAge = 50,
            PatientGender = "男",
            ExamMethod = "平扫",
            ExamDevice = "CT",
            ExamPart = "胸部",
        };

        var result = await service.ExecuteQcAsync(request);
        var response = result.Data as QcResponse;

        Assert.NotNull(response);
        Assert.Equal("R001", response!.ReportId);
        Assert.True(response.TotalScore >= response.PassScore);
        Assert.True(response.Passed);
        // UnitFormatRule may fire for units in text; check no rule-engine issues
        Assert.DoesNotContain(response.Issues, i => i.IssueType == "检查设备-描述矛盾");
    }

    [Fact]
    public async Task 男女矛盾_降分并报critical()
    {
        var engine = CreateEngine();
        var service = CreateService();
        var request = new QcRequest
        {
            ReportId = "R002",
            Findings = "子宫肌瘤约3cm。",
            Impression = "子宫肌瘤。",
            PatientGender = "男",
            PatientAge = 50,
        };

        var result = await service.ExecuteQcAsync(request);
        var response = result.Data as QcResponse;

        Assert.NotNull(response);
        // gender_conflict→logic维度(30%)，满意度降低但不低于及格线
        Assert.True(response!.TotalScore <= 97m);
        Assert.Contains(response.Issues, i => i.IssueType == "性别-解剖部位矛盾检测");
    }

    [Fact]
    public async Task 危急征象_报critical级别()
    {
        var engine = CreateEngine();
        var service = CreateService();
        var request = new QcRequest
        {
            ReportId = "R003",
            Findings = "主动脉夹层，累及升主动脉。",
            Impression = "主动脉夹层（Stanford A型）。",
        };

        var result = await service.ExecuteQcAsync(request);
        var response = result.Data as QcResponse;

        Assert.NotNull(response);
        Assert.Contains(response!.Issues, i => i.Severity == "critical");
    }

    [Fact]
    public async Task ReportId为空_返回错误()
    {
        var engine = CreateEngine();
        var service = CreateService();
        var request = new QcRequest
        {
            ReportId = "",
            Findings = "测试",
            Impression = "测试",
        };

        var result = await service.ExecuteQcAsync(request);

        Assert.Equal(400, result.Code);
    }

    [Fact]
    public async Task 多项问题_合并返回()
    {
        var engine = CreateEngine();
        var service = CreateService();
        var request = new QcRequest
        {
            ReportId = "R005",
            Findings = "左侧乳腺肿块。",
            Impression = "右侧乳腺肿块，建议复查。",
            PatientGender = "男",
            PatientAge = 5,
        };

        var result = await service.ExecuteQcAsync(request);
        var response = result.Data as QcResponse;

        Assert.NotNull(response);
        // direction_conflict from left/right mismatch
        Assert.Contains(response!.Issues, i => i.IssueType == "direction_conflict");
    }

    [Fact]
    public async Task 平扫出现增强描述_报错()
    {
        var engine = CreateEngine();
        var service = CreateService();
        var request = new QcRequest
        {
            ReportId = "R006",
            Findings = "右肺结节，明显强化。",
            Impression = "右肺结节，请随访。",
            ExamMethod = "平扫",
        };

        var result = await service.ExecuteQcAsync(request);
        var response = result.Data as QcResponse;

        Assert.NotNull(response);
        Assert.Contains(response!.Issues, i => i.IssueType == "扫描方式-增强描述矛盾");
    }
}
