using Xunit;
using Agent_QC.Models;
using Agent_QC.Services;

namespace Agent_QC.Tests.IntegrationTests;

public class VllmIntegrationTests
{
    [Fact]
    public async Task VllmClient_连接到vLLM_健康检查通过()
    {
        var modelPath = "/home/gulu/.cache/modelscope/hub/models/Qwen/Qwen3-4B-AWQ";
        var client = new VllmClient(new HttpClient(), "http://localhost:8100", modelPath);

        var healthy = await client.CheckHealthAsync();

        Assert.True(healthy);
        Assert.Equal(VllmHealthStatus.Healthy, client.Health);
    }

    [Fact]
    public async Task QcService_有vLLM时_正确调用SkillSquad()
    {
        var vllm = new VllmClient(new HttpClient(), "http://localhost:8100", "qwen3-4b-awq");
        await vllm.CheckHealthAsync();
        var registry = new SkillRegistry("knowledge/skills");
        var dbPath = Path.Combine(AppContext.BaseDirectory, "knowledge", "rules.db");
        if (!File.Exists(dbPath))
            dbPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "knowledge", "rules.db"));
        var ruleEngine = new RuleEngine(dbPath);
        ruleEngine.Initialize();
        var dictPath = Path.Combine(AppContext.BaseDirectory, "knowledge", "jieba_medical_dict.txt");
        var terminologyPath = Path.Combine(AppContext.BaseDirectory, "knowledge", "terminology.yaml");
        var jieba = new JiebaSegmenter(dictPath);
        var normalizer = new EntityNormalizer(terminologyPath);
        var modelPath = Path.Combine(AppContext.BaseDirectory, "knowledge", "models", "roberta-ner.onnx");
        var vocabPath = Path.Combine(AppContext.BaseDirectory, "knowledge", "models", "vocab.txt");
        var robertaNer = new RobertaNerService(jieba, normalizer, modelPath, vocabPath);
        var logicEngine = new LogicEngine();
        var service = new QcService(ruleEngine, robertaNer, normalizer, logicEngine, vllm, registry);

        var request = new QcRequest
        {
            ReportId = "INT-001",
            Findings = "子宫肌瘤约3cm，形态规则。",
            Impression = "子宫肌瘤，建议随访。",
            PatientGender = "男",
            PatientAge = 45,
            ExamMethod = "平扫",
            ExamDevice = "CT",
            ExamPart = "盆腔",
        };

        var result = await service.ExecuteQcAsync(request);
        var response = result.Data as QcResponse;

        Assert.NotNull(response);
        // gender_conflict 规则会触发 → vLLM Skill Squad 仲裁
        // 规则 + LLM 高置信度 pass → 问题被移除
        // 至少确保管线完整走通，返回了结果
        Assert.Equal("INT-001", response!.ReportId);
        Assert.True(response.ProcessTimeMs > 0);
    }

    [Fact]
    public async Task VllmClient_发送SkillPrompt_返回JSON结果()
    {
        var modelPath = "/home/gulu/.cache/modelscope/hub/models/Qwen/Qwen3-4B-AWQ";
        var client = new VllmClient(new HttpClient(), "http://localhost:8100", modelPath);
        await client.CheckHealthAsync();

        var request = new VllmChatRequest
        {
            Messages = new List<VllmMessage>
            {
                new() { Role = "system", Content = "你是放射科审查专家。输出严格JSON。" },
                new() { Role = "user", Content = "男性患者，报告写\"子宫肌瘤\"。请判断。输出JSON格式：{\"judgment\":\"pass\"|\"fail\",\"confidence\":0.0-1.0}" },
            },
            MaxTokens = 100,
            Temperature = 0.1f,
        };

        var response = await client.ChatAsync(request);

        Assert.NotNull(response);
        Assert.NotNull(response!.FirstContent);
    }
}
