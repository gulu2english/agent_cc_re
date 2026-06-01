using System.Text.Json;
using System.Text.RegularExpressions;
using Agent_QC.Models;

namespace Agent_QC.Services;

public partial class RuleExecutor
{
    private readonly TrieMatcher _trie;
    private readonly NegationDetector _negationDetector = new();

    public RuleExecutor(TrieMatcher trie) => _trie = trie;

    public List<QcIssueDto> Execute(QcRequest request, List<RuleDef> rules, Dictionary<int, RuleKeyword> keywordById)
    {
        var issues = new List<QcIssueDto>();
        var fullText = (request.Findings ?? "") + (request.Impression ?? "");
        var findings = request.Findings ?? "";
        var impression = request.Impression ?? "";

        var trieHits = _trie.FindAll(fullText);
        var hitsByRuleId = trieHits.GroupBy(h => h.RuleId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var rule in rules.Where(r => r.IsActive).OrderBy(r => r.SortOrder))
        {
            var ruleHits = hitsByRuleId.GetValueOrDefault(rule.Id, new List<TrieHit>());

            switch (rule.RuleType)
            {
                case "keyword_negation":
                    HandleKeywordNegation(rule, ruleHits, fullText, issues);
                    break;
                case "keyword_age":
                    HandleKeywordAge(rule, ruleHits, request, issues, keywordById);
                    break;
                case "keyword_device":
                    HandleKeywordDevice(rule, request, issues);
                    break;
                case "keyword_scan":
                    HandleKeywordScan(rule, ruleHits, request, issues);
                    break;
                case "keyword_replace":
                    HandleKeywordReplace(rule, ruleHits, issues, keywordById);
                    break;
                case "direction_compare":
                    HandleDirectionCompare(findings, impression, issues);
                    break;
                case "cross_section":
                    HandleCrossSection(rule, findings, impression, fullText, issues);
                    break;
                case "regex_duplicate":
                    HandleRegexDuplicate(rule, fullText, issues);
                    break;
                case "sentence_check":
                    HandleSentenceCheck(request, issues);
                    break;
                case "field_validation":
                    HandleFieldValidation(request, issues);
                    break;
            }
        }

        return issues;
    }

    private void HandleKeywordNegation(RuleDef rule, List<TrieHit> hits, string fullText, List<QcIssueDto> issues)
    {
        var excludeKeywords = rule.Keywords.Where(k => k.IsExclude).Select(k => k.Keyword).ToList();

        foreach (var hit in hits)
        {
            if (_negationDetector.IsNegated(fullText, hit.Keyword)) continue;
            if (HasExcludePattern(fullText, hit.Keyword, hit.StartPos, excludeKeywords)) continue;

            issues.Add(new QcIssueDto
            {
                IssueType = rule.Name,
                Severity = rule.Severity,
                Description = rule.Description?.Replace("{keyword}", hit.Keyword) ?? $"检测到「{hit.Keyword}」",
            });
        }
    }

    private static bool HasExcludePattern(string text, string keyword, int kwIdx, List<string> excludePatterns)
    {
        if (excludePatterns.Count == 0) return false;
        int start = Math.Max(0, kwIdx - 5);
        int end = Math.Min(text.Length, kwIdx + keyword.Length + 10);
        var context = text[start..end];
        return excludePatterns.Any(p => context.Contains(p, StringComparison.Ordinal));
    }

    private void HandleKeywordAge(RuleDef rule, List<TrieHit> hits, QcRequest request,
        List<QcIssueDto> issues, Dictionary<int, RuleKeyword> keywordById)
    {
        if (request.PatientAge == null) return;
        var age = request.PatientAge.Value;

        foreach (var hit in hits)
        {
            var extraData = keywordById.GetValueOrDefault(hit.KeywordId)?.ExtraData;
            if (string.IsNullOrEmpty(extraData)) continue;
            var parts = extraData.Split('-');
            if (parts.Length != 2) continue;
            if (!int.TryParse(parts[0], out var min) || !int.TryParse(parts[1], out var max)) continue;
            if (age < min || age > max) continue;

            issues.Add(new QcIssueDto
            {
                IssueType = rule.Name,
                Severity = rule.Severity,
                Description = $"患者年龄{age}岁，出现「{hit.Keyword}」需确认（一般{min}-{max}岁不常见）",
            });
        }
    }

    private void HandleKeywordDevice(RuleDef rule, QcRequest request, List<QcIssueDto> issues)
    {
        if (string.IsNullOrWhiteSpace(request.ExamDevice)) return;
        if (string.IsNullOrWhiteSpace(rule.ParamsJson)) return;

        var deviceMapping = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(rule.ParamsJson);
        if (deviceMapping == null) return;

        var fullText = (request.Findings ?? "") + (request.Impression ?? "");

        foreach (var (deviceKey, bannedTerms) in deviceMapping)
        {
            if (!request.ExamDevice.Contains(deviceKey, StringComparison.Ordinal)) continue;

            foreach (var term in bannedTerms)
            {
                if (fullText.Contains(term, StringComparison.Ordinal))
                {
                    issues.Add(new QcIssueDto
                    {
                        IssueType = rule.Name,
                        Severity = rule.Severity,
                        Description = $"检查设备为{request.ExamDevice}，但报告中出现「{term}」",
                    });
                }
            }
        }
    }

    private void HandleKeywordScan(RuleDef rule, List<TrieHit> hits, QcRequest request, List<QcIssueDto> issues)
    {
        if (string.IsNullOrWhiteSpace(request.ExamMethod)) return;
        if (string.IsNullOrWhiteSpace(rule.ParamsJson)) return;

        using var doc = JsonDocument.Parse(rule.ParamsJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("trigger_method", out var tm)) return;
        var triggerMethod = tm.GetString() ?? "";

        if (!request.ExamMethod.Contains(triggerMethod, StringComparison.Ordinal)) return;

        foreach (var hit in hits)
        {
            issues.Add(new QcIssueDto
            {
                IssueType = rule.Name,
                Severity = rule.Severity,
                Description = $"检查方式为{triggerMethod}，但报告中出现增强描述「{hit.Keyword}」",
            });
            break;
        }
    }

    private void HandleKeywordReplace(RuleDef rule, List<TrieHit> hits, List<QcIssueDto> issues,
        Dictionary<int, RuleKeyword> keywordById)
    {
        string subType = "unknown";
        if (!string.IsNullOrWhiteSpace(rule.ParamsJson))
        {
            using var doc = JsonDocument.Parse(rule.ParamsJson);
            subType = doc.RootElement.TryGetProperty("sub_type", out var st) ? st.GetString() ?? "unknown" : "unknown";
        }

        foreach (var hit in hits)
        {
            var suggestion = keywordById.GetValueOrDefault(hit.KeywordId)?.ExtraData ?? "建议修改";
            issues.Add(new QcIssueDto
            {
                IssueType = rule.Category == "level1_text" ? "text_error" : "terminology_error",
                SubType = subType,
                Severity = rule.Severity,
                OriginalText = hit.Keyword,
                SuggestedText = suggestion,
                Description = BuildReplaceDescription(subType, hit.Keyword, suggestion),
                Suggestion = BuildReplaceSuggestion(subType, hit.Keyword, suggestion),
            });
        }
    }

    private static string BuildReplaceDescription(string subType, string keyword, string suggestion) => subType switch
    {
        "phrase_typo" => $"疑似错别字「{keyword}」，应为「{suggestion}」",
        "anatomy_nonstandard" => $"解剖术语不标准「{keyword}」，建议使用「{suggestion}」",
        "colloquial" => $"报告中出现口语化/非客观描述「{keyword}」",
        _ => $"「{keyword}」应为规范术语「{suggestion}」",
    };

    private static string BuildReplaceSuggestion(string subType, string keyword, string suggestion) => subType switch
    {
        "phrase_typo" => $"请将「{keyword}」更正为「{suggestion}」",
        "anatomy_nonstandard" => $"请使用国际通用解剖命名「{suggestion}」代替「{keyword}」",
        "colloquial" => suggestion,
        _ => $"建议使用「{suggestion}」代替「{keyword}」",
    };

    private static readonly string[] LeftWords =
        { "左侧", "左叶", "左侧壁", "左肺", "左肾", "左上", "左下", "左前", "左后" };
    private static readonly string[] RightWords =
        { "右侧", "右叶", "右侧壁", "右肺", "右肾", "右上", "右下", "右前", "右后" };

    private void HandleDirectionCompare(string findings, string impression, List<QcIssueDto> issues)
    {
        bool findingsLeft = LeftWords.Any(f => findings.Contains(f, StringComparison.Ordinal));
        bool findingsRight = RightWords.Any(f => findings.Contains(f, StringComparison.Ordinal));
        bool impressionLeft = LeftWords.Any(f => impression.Contains(f, StringComparison.Ordinal));
        bool impressionRight = RightWords.Any(f => impression.Contains(f, StringComparison.Ordinal));

        if (findingsLeft && impressionRight && !impressionLeft)
        {
            issues.Add(new QcIssueDto
            {
                IssueType = "direction_conflict",
                Severity = "warning",
                Description = "影像所见为左侧，诊断结论为右侧，请确认方位是否准确",
            });
        }
        if (findingsRight && impressionLeft && !impressionRight)
        {
            issues.Add(new QcIssueDto
            {
                IssueType = "direction_conflict",
                Severity = "warning",
                Description = "影像所见为右侧，诊断结论为左侧，请确认方位是否准确",
            });
        }
    }

    private void HandleCrossSection(RuleDef rule, string findings, string impression, string fullText, List<QcIssueDto> issues)
    {
        if (string.IsNullOrWhiteSpace(rule.ParamsJson)) return;

        using var doc = JsonDocument.Parse(rule.ParamsJson);
        var root = doc.RootElement;

        var positiveSet = new List<string>();
        if (root.TryGetProperty("positive_set", out var ps))
            foreach (var e in ps.EnumerateArray())
                if (e.GetString() is { } s) positiveSet.Add(s);

        var negativeSet = new List<string>();
        if (root.TryGetProperty("negative_set", out var ns))
            foreach (var e in ns.EnumerateArray())
                if (e.GetString() is { } s) negativeSet.Add(s);

        var matchMode = root.TryGetProperty("match_mode", out var mm) ? mm.GetString() ?? "" : "";
        var searchSection = root.TryGetProperty("search_section", out var ss) ? ss.GetString() ?? "full_text" : "full_text";
        var secondarySection = root.TryGetProperty("secondary_section", out var ss2) ? ss2.GetString() ?? "impression" : "impression";

        var section = searchSection switch
        {
            "findings" => findings,
            "impression" => impression,
            _ => fullText,
        };
        var section2 = secondarySection switch
        {
            "findings" => findings,
            "impression" => impression,
            _ => impression,
        };

        bool hasA = positiveSet.Count == 0 || positiveSet.Any(kw => section.Contains(kw, StringComparison.Ordinal));
        bool hasB = negativeSet.Count == 0 || negativeSet.Any(kw => section2.Contains(kw, StringComparison.Ordinal));

        if ((matchMode == "if_A_then_B" || matchMode == "if_A_not_B") && hasA && !hasB)
        {
            issues.Add(new QcIssueDto
            {
                IssueType = rule.Category == "level2_semantic" ? "terminology_error" : rule.Name,
                SubType = rule.Name,
                Severity = rule.Severity,
                Description = rule.Description ?? "条件不满足",
                Suggestion = root.TryGetProperty("suggestion", out var sug) ? sug.GetString() : null,
            });
        }

        if (matchMode == "severity_compare" && hasA && hasB)
        {
            issues.Add(new QcIssueDto
            {
                IssueType = "semantic_conflict",
                SubType = rule.Name,
                Severity = rule.Severity,
                Description = rule.Description ?? "程度矛盾",
                Suggestion = root.TryGetProperty("suggestion", out var sug) ? sug.GetString() : null,
            });
        }
    }

    [GeneratedRegex(@"([\p{IsCJKUnifiedIdeographs}])\1{1,}", RegexOptions.Compiled)]
    private static partial Regex DupPattern();

    private void HandleRegexDuplicate(RuleDef rule, string fullText, List<QcIssueDto> issues)
    {
        var validDuplicates = new HashSet<char>();
        if (!string.IsNullOrWhiteSpace(rule.ParamsJson))
        {
            using var doc = JsonDocument.Parse(rule.ParamsJson);
            if (doc.RootElement.TryGetProperty("valid_duplicates", out var vd))
            {
                if (vd.ValueKind == JsonValueKind.String)
                {
                    var str = vd.GetString();
                    if (str != null)
                        foreach (var c in str)
                            validDuplicates.Add(c);
                }
                else if (vd.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in vd.EnumerateArray())
                        if (c.GetString() is { Length: > 0 } s) validDuplicates.Add(s[0]);
                }
            }
        }

        foreach (Match m in DupPattern().Matches(fullText))
        {
            var dupChar = m.Value[0];
            if (validDuplicates.Contains(dupChar)) continue;

            issues.Add(new QcIssueDto
            {
                IssueType = "text_error",
                SubType = "duplicate_char",
                Severity = rule.Severity,
                OriginalText = m.Value,
                SuggestedText = dupChar.ToString(),
                Description = $"疑似重复字「{m.Value}」",
                Suggestion = $"请检查是否误写了两次「{dupChar}」",
            });
        }
    }

    private static readonly HashSet<char> ValidEndings = new() { '。', '）', '；', '：', '！', '？', '.' };
    private static readonly char[] ListStarters = { '-', '•', '·' };

    private void HandleSentenceCheck(QcRequest request, List<QcIssueDto> issues)
    {
        CheckSection(request.Findings ?? "", "findings", issues);
        CheckSection(request.Impression ?? "", "impression", issues);
    }

    private static void CheckSection(string text, string location, List<QcIssueDto> issues)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var sentences = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var sentence in sentences)
        {
            var trimmed = sentence.Trim();
            if (trimmed.Length < 4) continue;
            if (ListStarters.Any(c => trimmed.StartsWith(c))) continue;
            if (char.IsDigit(trimmed[0])) continue;

            if (!ValidEndings.Contains(trimmed[^1]))
            {
                issues.Add(new QcIssueDto
                {
                    IssueType = "text_error",
                    SubType = "missing_period",
                    Severity = "warning",
                    OriginalText = trimmed.Length > 50 ? trimmed[..50] + "..." : trimmed,
                    Description = "句末缺少标点符号（。；：等）",
                    Suggestion = "请在句末添加句号（。）",
                    Location = location,
                });
                break;
            }
        }
    }

    private void HandleFieldValidation(QcRequest request, List<QcIssueDto> issues)
    {
        if (!string.IsNullOrWhiteSpace(request.PatientGender)
            && request.PatientGender != "男" && request.PatientGender != "女")
        {
            issues.Add(new QcIssueDto
            {
                IssueType = "completeness_error",
                SubType = "invalid_field",
                Severity = "error",
                Description = $"患者性别「{request.PatientGender}」不在标准范围内（男/女）",
                Suggestion = "性别应为「男」或「女」",
            });
        }

        if (request.PatientAge is < 0 or > 150)
        {
            issues.Add(new QcIssueDto
            {
                IssueType = "completeness_error",
                SubType = "invalid_field",
                Severity = "error",
                Description = $"患者年龄 {request.PatientAge} 不在合理范围（0-150）",
                Suggestion = "请核实患者年龄",
            });
        }

        if (string.IsNullOrWhiteSpace(request.ExamPart))
        {
            issues.Add(new QcIssueDto
            {
                IssueType = "completeness_error",
                SubType = "missing_required_field",
                Severity = "warning",
                Description = "检查部位不能为空",
                Suggestion = "请填写检查部位",
            });
        }

        if (string.IsNullOrWhiteSpace(request.ExamDevice))
        {
            issues.Add(new QcIssueDto
            {
                IssueType = "completeness_error",
                SubType = "missing_required_field",
                Severity = "warning",
                Description = "检查设备不能为空",
                Suggestion = "请填写检查设备",
            });
        }
    }
}
