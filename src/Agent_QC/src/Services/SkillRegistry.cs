using Agent_QC.Models;

namespace Agent_QC.Services;

public class SkillRegistry
{
    private readonly Dictionary<string, (string System, string User)> _prompts = new();

    public IReadOnlyList<string> SkillIds => _prompts.Keys.ToList().AsReadOnly();

    public SkillRegistry(string skillDir = "knowledge/skills")
    {
        var resolved = ResolveSkillDir(skillDir);
        if (resolved == null)
            return;

        foreach (var systemFile in Directory.GetFiles(resolved, "*-system.md"))
        {
            var fileName = Path.GetFileNameWithoutExtension(systemFile); // e.g. "gender-anatomy-checker-system"
            var skillId = fileName[..^"-system".Length];                 // e.g. "gender-anatomy-checker"
            var userFile = Path.Combine(resolved, $"{skillId}-user.md");

            var system = File.ReadAllText(systemFile).Trim();
            var user = File.Exists(userFile) ? File.ReadAllText(userFile).Trim() : "";
            _prompts[skillId] = (system, user);
        }
    }

    private static string? ResolveSkillDir(string skillDir)
    {
        if (Path.IsPathRooted(skillDir) && Directory.Exists(skillDir))
            return skillDir;

        // Try relative to base directory
        var fromBase = Path.Combine(AppContext.BaseDirectory, skillDir);
        if (Directory.Exists(fromBase))
            return fromBase;

        // Walk up from base directory to find knowledge/skills
        var current = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(current, skillDir);
            if (Directory.Exists(candidate))
                return candidate;
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        return null;
    }

    public bool HasSkill(string skillId) => _prompts.ContainsKey(skillId);

    public string BuildSystemPrompt(string skillId)
    {
        return _prompts.TryGetValue(skillId, out var p) ? p.System : "";
    }

    public string BuildUserPrompt(string skillId, QcRequest request)
    {
        if (!_prompts.TryGetValue(skillId, out var p))
            return "";

        return p.User
            .Replace("{PatientGender}", request.PatientGender ?? "")
            .Replace("{PatientAge}", request.PatientAge?.ToString() ?? "")
            .Replace("{Findings}", request.Findings ?? "")
            .Replace("{Impression}", request.Impression ?? "")
            .Replace("{ExamPart}", request.ExamPart ?? "")
            .Replace("{ExamDevice}", request.ExamDevice ?? "")
            .Replace("{ExamMethod}", request.ExamMethod ?? "")
            .Replace("{ReportType}", request.ReportType ?? "")
            .Replace("{ClinicalDiagnosis}", request.ClinicalDiagnosis ?? "");
    }
}
