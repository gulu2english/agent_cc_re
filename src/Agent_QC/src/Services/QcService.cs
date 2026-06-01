using System.Diagnostics;
using Agent_QC.Models;
using Agent_QC.Services.Rules.Level1;

namespace Agent_QC.Services;

public class QcService : IQcService
{
    // Level 0: preprocessing
    private readonly SectionParser _sectionParser = new();
    private readonly JiebaSegmenter _jieba;

    // Rule engine (replaces ALL Level 1-4 individual rule classes)
    private readonly RuleEngine _ruleEngine;

    // Level 2: RoBERTa NER + Logic Engine
    private readonly RobertaNerService _robertaNer;
    private readonly EntityNormalizer _entityNormalizer;
    private readonly LogicEngine _logicEngine;

    // Measurement unit (preserved — not migrated)
    private readonly UnitFormatRule _unitFormatRule = new();

    // Hermes Skill Squad (unchanged)
    private readonly IVllmClient _vllm;
    private readonly SkillRegistry _skillRegistry;
    private readonly HermesOrchestrator _orchestrator;
    private readonly QaArbiter _arbiter;

    private readonly ScoringEngine _scoringEngine = new();

    public QcService(RuleEngine ruleEngine,
        RobertaNerService robertaNer,
        EntityNormalizer entityNormalizer,
        LogicEngine logicEngine,
        IVllmClient? vllm = null,
        SkillRegistry? skillRegistry = null, JiebaSegmenter? jieba = null)
    {
        _ruleEngine = ruleEngine;
        _robertaNer = robertaNer;
        _entityNormalizer = entityNormalizer;
        _logicEngine = logicEngine;
        _vllm = vllm ?? new VllmClient(new HttpClient(), "http://localhost:8100");
        _skillRegistry = skillRegistry ?? new SkillRegistry();
        _jieba = jieba ?? new JiebaSegmenter("knowledge/jieba_medical_dict.txt");
        _orchestrator = new HermesOrchestrator(_vllm, _skillRegistry);
        _arbiter = new QaArbiter();
    }

    public async Task<AjaxResult> ExecuteQcAsync(QcRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ReportId))
            return AjaxResult.Error(400, "ReportId cannot be empty");

        var sw = Stopwatch.StartNew();
        var issues = new List<QcIssueDto>();

        // Level 0: preprocessing
        request.SegmentedFindings = _jieba.Segment(request.Findings ?? "");
        request.SegmentedImpression = _jieba.Segment(request.Impression ?? "");

        // Level 1: unified rule engine
        issues.AddRange(_ruleEngine.Execute(request));

        // Level 2: RoBERTa NER + Logic Engine
        var findingsEntities = _robertaNer.Extract(request.Findings ?? "");
        var impressionEntities = _robertaNer.Extract(request.Impression ?? "");
        var nFindings = _entityNormalizer.Normalize(findingsEntities);
        var nImpression = _entityNormalizer.Normalize(impressionEntities);
        issues.AddRange(_logicEngine.Compare(request, nFindings, nImpression, issues));

        // Measurement unit check (preserved separately)
        issues.AddRange(_unitFormatRule.Check(request));

        // Skill Squad (unchanged)
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
            ? "No issues found"
            : $"Found {issues.Count} issues";

        return AjaxResult.Success(response);
    }
}
