using Xunit;
using Agent_QC.Models;
using Agent_QC.Services;

namespace Agent_QC.Tests.UnitTests.Services;

public class LogicEngineTests
{
    private readonly LogicEngine _engine = new();

    private static List<NerEntity> Anatomy(string text) => new()
    {
        new() { Type = "anatomy", Text = text, Normalized = text, Start = 0, End = text.Length, Confidence = 0.9f },
    };

    private static List<NerEntity> Direction(string text) => new()
    {
        new() { Type = "direction", Text = text, Normalized = text, Start = 0, End = text.Length, Confidence = 0.95f },
    };

    private static List<NerEntity> Empty() => new();

    // ── DirectionConflict ──────────────────────────────

    [Fact]
    public void DirectionConflict_FindingsLeft_ImpressionRight_ReportsIssue()
    {
        var f = Direction("左侧");
        var i = Direction("右侧");
        var issues = _engine.Compare(new QcRequest(), f, i, new List<QcIssueDto>());
        Assert.Contains(issues, x => x.IssueType == "direction_conflict");
    }

    [Fact]
    public void DirectionConflict_FindingsRight_ImpressionLeft_ReportsIssue()
    {
        var f = Direction("右侧");
        var i = Direction("左侧");
        var issues = _engine.Compare(new QcRequest(), f, i, new List<QcIssueDto>());
        Assert.Contains(issues, x => x.IssueType == "direction_conflict");
    }

    [Fact]
    public void DirectionConflict_BothSameDirection_NoIssue()
    {
        var f = Direction("左侧");
        var i = Direction("左侧");
        var issues = _engine.Compare(new QcRequest(), f, i, new List<QcIssueDto>());
        Assert.DoesNotContain(issues, x => x.IssueType == "direction_conflict");
    }

    [Fact]
    public void DirectionConflict_Bilateral_Neutralizes_NoIssue()
    {
        var f = Direction("双侧");
        var i = Direction("右侧");
        var issues = _engine.Compare(new QcRequest(), f, i, new List<QcIssueDto>());
        Assert.DoesNotContain(issues, x => x.IssueType == "direction_conflict");
    }

    [Fact]
    public void DirectionConflict_NoDirectionEntities_NoIssue()
    {
        var f = Anatomy("肺部");
        var i = Anatomy("肝脏");
        var issues = _engine.Compare(new QcRequest(), f, i, new List<QcIssueDto>());
        Assert.DoesNotContain(issues, x => x.IssueType == "direction_conflict");
    }

    [Fact]
    public void DirectionConflict_BothSidesInSameSection_NoIssue()
    {
        var f = new List<NerEntity> { Direction("左侧")[0], Direction("右侧")[0] };
        var i = Direction("左侧");
        var issues = _engine.Compare(new QcRequest(), f, i, new List<QcIssueDto>());
        Assert.DoesNotContain(issues, x => x.IssueType == "direction_conflict");
    }

    // ── GenderAnatomyConflict ──────────────────────────

    [Fact]
    public void GenderAnatomy_MaleWithUterus_ReportsError()
    {
        var f = Anatomy("子宫");
        var issues = _engine.Compare(
            new QcRequest { PatientGender = "男" }, f, Empty(), new List<QcIssueDto>());
        Assert.Contains(issues, x => x.IssueType == "gender_anatomy_conflict" && x.Severity == "error");
    }

    [Fact]
    public void GenderAnatomy_FemaleWithProstate_ReportsError()
    {
        var f = Anatomy("前列腺");
        var issues = _engine.Compare(
            new QcRequest { PatientGender = "女" }, f, Empty(), new List<QcIssueDto>());
        Assert.Contains(issues, x => x.IssueType == "gender_anatomy_conflict" && x.Severity == "error");
    }

    [Fact]
    public void GenderAnatomy_MaleWithLung_NoIssue()
    {
        var f = Anatomy("肺部");
        var issues = _engine.Compare(
            new QcRequest { PatientGender = "男" }, f, Empty(), new List<QcIssueDto>());
        Assert.DoesNotContain(issues, x => x.IssueType == "gender_anatomy_conflict");
    }

    [Fact]
    public void GenderAnatomy_NoGender_NoIssue()
    {
        var f = Anatomy("子宫");
        var issues = _engine.Compare(
            new QcRequest { PatientGender = null }, f, Empty(), new List<QcIssueDto>());
        Assert.DoesNotContain(issues, x => x.IssueType == "gender_anatomy_conflict");
    }

    [Fact]
    public void GenderAnatomy_OnlyOneIssuePerReport()
    {
        var f = new List<NerEntity>
        {
            new() { Type = "anatomy", Text = "子宫", Normalized = "子宫", Start = 0, End = 2, Confidence = 0.9f },
            new() { Type = "anatomy", Text = "卵巢", Normalized = "卵巢", Start = 5, End = 7, Confidence = 0.9f },
        };
        var issues = _engine.Compare(
            new QcRequest { PatientGender = "男" }, f, Empty(), new List<QcIssueDto>());
        var genderIssues = issues.Where(x => x.IssueType == "gender_anatomy_conflict").ToList();
        Assert.Single(genderIssues);
    }

    // ── SiteOmission ───────────────────────────────────

    [Fact]
    public void SiteOmission_SiteInFindings_NotInImpression_ReportsWarning()
    {
        var f = Anatomy("肝脏");
        var i = Anatomy("胆囊");
        var issues = _engine.Compare(
            new QcRequest { Findings = "肝脏可见结节" }, f, i, new List<QcIssueDto>());
        Assert.Contains(issues, x => x.IssueType == "site_omission" && x.OriginalText == "肝脏");
    }

    [Fact]
    public void SiteOmission_SiteInBoth_NoIssue()
    {
        var f = Anatomy("肝脏");
        var i = Anatomy("肝脏");
        var issues = _engine.Compare(
            new QcRequest { Findings = "肝脏可见结节" }, f, i, new List<QcIssueDto>());
        Assert.DoesNotContain(issues, x => x.IssueType == "site_omission");
    }

    [Fact]
    public void SiteOmission_NegatedSite_NoIssue()
    {
        var f = Anatomy("肝脏");
        var issues = _engine.Compare(
            new QcRequest { Findings = "未见肝脏明显异常" }, f, Empty(), new List<QcIssueDto>());
        Assert.DoesNotContain(issues, x => x.IssueType == "site_omission");
    }

    [Fact]
    public void SiteOmission_NoAnatomyEntities_NoIssue()
    {
        var f = new List<NerEntity>
        {
            new() { Type = "direction", Text = "左侧", Normalized = "左侧", Start = 0, End = 2, Confidence = 0.95f },
        };
        var issues = _engine.Compare(new QcRequest(), f, Empty(), new List<QcIssueDto>());
        Assert.DoesNotContain(issues, x => x.IssueType == "site_omission");
    }

    // ── ExamConsistency ────────────────────────────────

    [Fact]
    public void ExamConsistency_CTDevice_MRITerm_ReportsError()
    {
        var f = new List<NerEntity>
        {
            new() { Type = "finding", Text = "T1WI低信号", Normalized = "T1WI低信号", Start = 0, End = 6, Confidence = 0.9f },
        };
        var issues = _engine.Compare(
            new QcRequest { ExamDevice = "CT" }, f, Empty(), new List<QcIssueDto>());
        Assert.Contains(issues, x => x.IssueType == "exam_device_conflict" && x.Severity == "error");
    }

    [Fact]
    public void ExamConsistency_ProperDevice_NoIssue()
    {
        var f = new List<NerEntity>
        {
            new() { Type = "finding", Text = "CT值50HU", Normalized = "CT值50HU", Start = 0, End = 8, Confidence = 0.9f },
        };
        var issues = _engine.Compare(
            new QcRequest { ExamDevice = "CT" }, f, Empty(), new List<QcIssueDto>());
        Assert.DoesNotContain(issues, x => x.IssueType == "exam_device_conflict");
    }

    [Fact]
    public void ExamConsistency_NoDevice_NoIssue()
    {
        var f = new List<NerEntity>
        {
            new() { Type = "finding", Text = "T1WI低信号", Normalized = "T1WI低信号", Start = 0, End = 6, Confidence = 0.9f },
        };
        var issues = _engine.Compare(
            new QcRequest { ExamDevice = null }, f, Empty(), new List<QcIssueDto>());
        Assert.DoesNotContain(issues, x => x.IssueType == "exam_device_conflict");
    }

    // ── Deduplication ──────────────────────────────────

    [Fact]
    public void Deduplicate_SameIssueTypeAndText_SkipsDuplicate()
    {
        // DirectionConflict sets Description with side info, match on that
        var f = Direction("左侧");
        var i = Direction("右侧");
        var existing = new List<QcIssueDto>
        {
            new() { IssueType = "direction_conflict", Description = "影像所见为左侧，诊断结论为右侧，请确认方位是否准确" },
        };
        var issues = _engine.Compare(new QcRequest(), f, i, existing);
        Assert.DoesNotContain(issues, x => x.IssueType == "direction_conflict");
    }

    [Fact]
    public void Deduplicate_DifferentIssueType_NotSkipped()
    {
        var f = Direction("左侧");
        var i = Direction("右侧");
        var existing = new List<QcIssueDto>
        {
            new() { IssueType = "other_issue", OriginalText = "左侧" },
        };
        var issues = _engine.Compare(new QcRequest(), f, i, existing);
        Assert.Contains(issues, x => x.IssueType == "direction_conflict");
    }
}
