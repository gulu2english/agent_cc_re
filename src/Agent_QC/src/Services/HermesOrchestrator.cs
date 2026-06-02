using System.Text.Json;
using Agent_QC.Models;

namespace Agent_QC.Services;

public class HermesOrchestrator
{
    private readonly IVllmClient _vllm;
    private readonly SkillRegistry _registry;

    private static readonly Dictionary<string, string> IssueToSkill = new()
    {
        // wzx 手动去掉，直接通过规则进行处理，不适用于llm中实现
        //["gender_conflict"] = "gender-anatomy-checker",
       // ["direction_conflict"] = "site-consistency-checker",
        ["semantic_conflict"] = "findings-impression-nli",
        ["critical_sign"] = "critical-sign-arbiter",
        ["device_conflict"] = "device-method-validator",
        ["scan_enhance_conflict"] = "device-method-validator",
        ["completeness_error"] = "measurement-completeness",
        ["rads_missing"] = "rads-compliance-checker",
        //["terminology_nonstandard"] = "terminology-validator",
        ["text_error"] = "terminology-validator",
        ["colloquial"] = "terminology-validator",
    };

    public HermesOrchestrator(IVllmClient vllm, SkillRegistry registry)
    {
        _vllm = vllm;
        _registry = registry;
    }

    /// <summary>根据规则命中结果，按需触发 Skill 并行推理。</summary>
    public async Task<List<SkillResult>> DispatchAsync(
        QcRequest request, List<QcIssueDto> ruleIssues, CancellationToken ct)
    {
        if (_vllm.Health != VllmHealthStatus.Healthy)
            return new();

        var skillIds = SelectSkills(ruleIssues);
        if (skillIds.Count == 0)
            return new();

        var tasks = skillIds.Select(id => CallSkillAsync(id, request, ct));
        var results = await Task.WhenAll(tasks);
        return results.Where(r => r != null).ToList()!;
    }

    private static List<string> SelectSkills(List<QcIssueDto> issues)
    {
        var skills = new HashSet<string>();
        foreach (var issue in issues)
        {
            if (!string.IsNullOrEmpty(issue.IssueType)
                && IssueToSkill.TryGetValue(issue.IssueType, out var skillId)
                && !string.IsNullOrEmpty(skillId))
            {
                skills.Add(skillId);
            }
        }
        return skills.ToList();
    }

    private async Task<SkillResult?> CallSkillAsync(
        string skillId, QcRequest request, CancellationToken ct)
    {
        if (!_registry.HasSkill(skillId))
            return null;

        var systemPrompt = _registry.BuildSystemPrompt(skillId);
        var userPrompt = _registry.BuildUserPrompt(skillId, request);
        if (string.IsNullOrEmpty(systemPrompt) || string.IsNullOrEmpty(userPrompt))
            return null;

        var chatRequest = new VllmChatRequest
        {
            Messages = new List<VllmMessage>
            {
                new() { Role = "system", Content = systemPrompt },
                new() { Role = "user", Content = userPrompt },
            },
            ResponseFormat = new ResponseFormat { Type = "json_object" },
        };

        var response = await _vllm.ChatAsync(chatRequest, ct);
        return SkillResult.FromJson(skillId, response?.FirstContent);
    }
}
