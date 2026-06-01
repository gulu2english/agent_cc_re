using Agent_QC.Models;

namespace Agent_QC.Services;

/// <summary>Deterministic LINQ entity comparators for Level 2 QC checks.</summary>
public class LogicEngine
{
    // direction word sets
    private static readonly HashSet<string> LeftWords = new(StringComparer.Ordinal)
    {
        "左侧", "左叶", "左侧壁", "左肺", "左肾", "左上", "左下", "左前", "左后",
        "左", "左半", "左缘", "左旁", "左外侧", "左内侧", "左上部", "左下部",
        "左肺上叶", "左肺下叶", "左上肺", "左下肺", "左膈", "左胸",
    };

    private static readonly HashSet<string> RightWords = new(StringComparer.Ordinal)
    {
        "右侧", "右叶", "右侧壁", "右肺", "右肾", "右上", "右下", "右前", "右后",
        "右", "右半", "右缘", "右旁", "右外侧", "右内侧", "右上部", "右下部",
        "右肺上叶", "右肺中叶", "右肺下叶", "右上肺", "右下肺", "右膈", "右胸",
    };

    private static readonly HashSet<string> BothWords = new(StringComparer.Ordinal)
    {
        "双侧", "双叶", "双肺", "双肾", "两边", "左右", "两侧",
    };

    // female-only anatomy (appears in reports of female patients only)
    private static readonly HashSet<string> FemaleAnatomy = new(StringComparer.Ordinal)
    {
        "子宫", "宫颈", "卵巢", "输卵管", "附件", "乳腺", "乳房", "阴道",
        "子宫内膜", "子宫肌瘤", "子宫体", "子宫颈", "宫体", "宫腔", "宫底",
        "宫颈管", "卵泡", "黄体", "胎盘", "羊水", "脐带",
    };

    // male-only anatomy
    private static readonly HashSet<string> MaleAnatomy = new(StringComparer.Ordinal)
    {
        "前列腺", "精囊", "附睾", "睾丸", "阴囊", "阴茎", "输精管", "精索",
    };

    // negation patterns — if the entity's sentence contains these, skip SiteOmission
    private static readonly string[] NegationPhrases =
    {
        "未见明确", "未见明显", "未见", "未显示", "未发现", "无明显",
        "未见确切", "未及", "未探及", "无异常", "未见异常",
    };

    /// <summary>Run all comparators. Deduplicates against existing Level 1 issues.</summary>
    public List<QcIssueDto> Compare(
        QcRequest request,
        List<NerEntity> findingsEntities,
        List<NerEntity> impressionEntities,
        List<QcIssueDto> existingIssues)
    {
        var issues = new List<QcIssueDto>();

        DirectionConflict(findingsEntities, impressionEntities, issues);
        GenderAnatomyConflict(request, findingsEntities, impressionEntities, issues);
        SiteOmission(findingsEntities, impressionEntities, request.Findings ?? "", issues);
        ExamConsistency(request, findingsEntities, impressionEntities, issues);

        // deduplicate: remove issues already caught by Level 1
        return issues
            .Where(i => !existingIssues.Any(e =>
                e.IssueType == i.IssueType &&
                e.OriginalText == i.OriginalText))
            .ToList();
    }

    // ── DirectionConflict ──────────────────────────────

    private void DirectionConflict(
        List<NerEntity> findingsEntities,
        List<NerEntity> impressionEntities,
        List<QcIssueDto> issues)
    {
        var findingsTexts = findingsEntities.Select(e => e.Normalized).ToHashSet(StringComparer.Ordinal);
        var impressionTexts = impressionEntities.Select(e => e.Normalized).ToHashSet(StringComparer.Ordinal);

        bool findingsHasLeft = findingsTexts.Any(t => LeftWords.Contains(t));
        bool findingsHasRight = findingsTexts.Any(t => RightWords.Contains(t));
        bool impressionHasLeft = impressionTexts.Any(t => LeftWords.Contains(t));
        bool impressionHasRight = impressionTexts.Any(t => RightWords.Contains(t));

        // bilateral neutralizes direction check
        bool anyBilateral = findingsTexts.Any(t => BothWords.Contains(t))
                         || impressionTexts.Any(t => BothWords.Contains(t));
        if (anyBilateral) return;

        if (findingsHasLeft && !findingsHasRight && impressionHasRight && !impressionHasLeft)
        {
            issues.Add(new QcIssueDto
            {
                IssueType = "direction_conflict",
                Severity = "warning",
                Description = "影像所见为左侧，诊断结论为右侧，请确认方位是否准确",
                Suggestion = "请核对左右方位描述",
            });
        }

        if (findingsHasRight && !findingsHasLeft && impressionHasLeft && !impressionHasRight)
        {
            issues.Add(new QcIssueDto
            {
                IssueType = "direction_conflict",
                Severity = "warning",
                Description = "影像所见为右侧，诊断结论为左侧，请确认方位是否准确",
                Suggestion = "请核对左右方位描述",
            });
        }
    }

    // ── GenderAnatomyConflict ──────────────────────────

    private void GenderAnatomyConflict(
        QcRequest request,
        List<NerEntity> findingsEntities,
        List<NerEntity> impressionEntities,
        List<QcIssueDto> issues)
    {
        if (string.IsNullOrWhiteSpace(request.PatientGender)) return;
        var gender = request.PatientGender;
        var allEntities = findingsEntities.Concat(impressionEntities).ToList();

        if (gender == "男")
        {
            foreach (var e in allEntities)
            {
                var check = string.IsNullOrEmpty(e.Normalized) ? e.Text : e.Normalized;
                if (FemaleAnatomy.Contains(check))
                {
                    issues.Add(new QcIssueDto
                    {
                        IssueType = "gender_anatomy_conflict",
                        Severity = "error",
                        Description = $"男性患者报告中出现女性解剖部位「{e.Text}」",
                        OriginalText = e.Text,
                        Suggestion = "请核实患者性别或解剖部位描述",
                    });
                    break; // one issue per report is enough
                }
            }
        }
        else if (gender == "女")
        {
            foreach (var e in allEntities)
            {
                var check = string.IsNullOrEmpty(e.Normalized) ? e.Text : e.Normalized;
                if (MaleAnatomy.Contains(check))
                {
                    issues.Add(new QcIssueDto
                    {
                        IssueType = "gender_anatomy_conflict",
                        Severity = "error",
                        Description = $"女性患者报告中出现男性解剖部位「{e.Text}」",
                        OriginalText = e.Text,
                        Suggestion = "请核实患者性别或解剖部位描述",
                    });
                    break;
                }
            }
        }
    }

    // ── SiteOmission ───────────────────────────────────

    private void SiteOmission(
        List<NerEntity> findingsEntities,
        List<NerEntity> impressionEntities,
        string findingsText,
        List<QcIssueDto> issues)
    {
        var findingsAnatomy = findingsEntities
            .Where(e => e.Type == "anatomy")
            .Select(e => string.IsNullOrEmpty(e.Normalized) ? e.Text : e.Normalized)
            .ToHashSet(StringComparer.Ordinal);

        var impressionAnatomy = impressionEntities
            .Where(e => e.Type == "anatomy")
            .Select(e => string.IsNullOrEmpty(e.Normalized) ? e.Text : e.Normalized)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var site in findingsAnatomy)
        {
            if (impressionAnatomy.Contains(site)) continue;

            // check if the site's sentence is negated
            if (IsSiteNegated(findingsText, site)) continue;

            issues.Add(new QcIssueDto
            {
                IssueType = "site_omission",
                Severity = "warning",
                Description = $"影像所见提及「{site}」，但诊断结论中未提及该部位",
                OriginalText = site,
                Suggestion = $"请在诊断结论中补充对「{site}」的描述",
            });
        }
    }

    private static bool IsSiteNegated(string text, string site)
    {
        var idx = text.IndexOf(site, StringComparison.Ordinal);
        if (idx < 0) return false;
        var before = text.AsSpan(Math.Max(0, idx - 15), Math.Min(idx, 15));
        var beforeStr = before.ToString();
        return NegationPhrases.Any(n => beforeStr.Contains(n, StringComparison.Ordinal));
    }

    // ── ExamConsistency ────────────────────────────────

    private void ExamConsistency(
        QcRequest request,
        List<NerEntity> findingsEntities,
        List<NerEntity> impressionEntities,
        List<QcIssueDto> issues)
    {
        if (string.IsNullOrWhiteSpace(request.ExamDevice)) return;

        var allEntities = findingsEntities.Concat(impressionEntities).ToList();
        var examTexts = allEntities
            .Where(e => e.Type == "finding" || e.Type == "anatomy") // exam method often tagged as finding
            .Select(e => e.Text)
            .ToHashSet(StringComparer.Ordinal);

        var device = request.ExamDevice;

        // MRI terms in CT report
        if (device.Contains("CT", StringComparison.Ordinal) && !device.Contains("MRI", StringComparison.Ordinal))
        {
            if (examTexts.Any(t => t.Contains("MRI") || t.Contains("T1WI") || t.Contains("T2WI")))
            {
                issues.Add(new QcIssueDto
                {
                    IssueType = "exam_device_conflict",
                    Severity = "error",
                    Description = $"检查设备为CT，但报告中出现MRI相关术语",
                    Suggestion = "请核实检查设备或报告术语",
                });
            }
        }

        // CT terms in MRI report
        if (device.Contains("MR", StringComparison.Ordinal) || device.Contains("MRI", StringComparison.Ordinal))
        {
            if (examTexts.Any(t => t.Contains("CT值") || t.Contains("HU")))
            {
                issues.Add(new QcIssueDto
                {
                    IssueType = "exam_device_conflict",
                    Severity = "error",
                    Description = $"检查设备为MRI，但报告中出现CT值相关术语",
                    Suggestion = "请核实检查设备或报告术语",
                });
            }
        }
    }
}
