using Xunit;
using Agent_QC.Models;
using Agent_QC.Services;

namespace Agent_QC.Tests.UnitTests.Services;

public class RuleEngineTests
{
    private RuleEngine CreateEngine()
    {
        // rules.db is copied to output by the test csproj Content rule
        var dbPath = Path.Combine(AppContext.BaseDirectory, "knowledge", "rules.db");
        if (!File.Exists(dbPath))
        {
            // Fallback: look relative to project root during development
            dbPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "knowledge", "rules.db"));
        }
        if (!File.Exists(dbPath))
        {
            throw new FileNotFoundException($"rules.db not found. Checked: {dbPath}");
        }
        var engine = new RuleEngine(dbPath);
        engine.Initialize();
        return engine;
    }

    // keyword_negation: 性别-解剖部位矛盾检测 (rule 1)
    [Fact]
    public void MalePatientWithUterus_ReportsCritical()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { PatientGender = "男", Findings = "子宫肌瘤" });
        Assert.Contains(result, i => i.IssueType == "性别-解剖部位矛盾检测" && i.Severity == "critical");
    }

    [Fact]
    public void MalePatient_NegatedUterus_NoIssue()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { PatientGender = "男", Findings = "未见子宫及附件异常" });
        Assert.DoesNotContain(result, i => i.IssueType == "性别-解剖部位矛盾检测");
    }

    [Fact]
    public void FemalePatientWithProstate_ReportsCritical()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { PatientGender = "女", Findings = "前列腺增生" });
        Assert.Contains(result, i => i.IssueType == "性别-解剖部位矛盾检测");
    }

    [Fact]
    public void PostSurgeryExcludePattern_NoIssue()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { PatientGender = "男", Findings = "子宫切除术后复查" });
        Assert.DoesNotContain(result, i => i.IssueType == "性别-解剖部位矛盾检测");
    }

    // keyword_negation: 危急征象检测 (rule 2)
    [Fact]
    public void CriticalSign_BrainHemorrhage_ReportsCritical()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { Findings = "脑出血" });
        Assert.Contains(result, i => i.IssueType == "危急征象检测" && i.Severity == "critical");
    }

    [Fact]
    public void CriticalSign_Negated_NoIssue()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { Findings = "未见明确脑出血" });
        Assert.DoesNotContain(result, i => i.IssueType == "危急征象检测");
    }

    // keyword_age: 年龄-诊断矛盾检测 (rule 3)
    [Fact]
    public void AgeConflict_TenYearOldOsteoporosis_ReportsIssue()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { PatientAge = 10, Findings = "骨质疏松" });
        Assert.Contains(result, i => i.IssueType == "年龄-诊断矛盾检测");
    }

    [Fact]
    public void AgeConflict_SeventyYearOld_NoIssue()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { PatientAge = 70, Findings = "骨质疏松" });
        Assert.DoesNotContain(result, i => i.IssueType == "年龄-诊断矛盾检测");
    }

    [Fact]
    public void AgeConflict_NoAge_Skip()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { PatientAge = null, Findings = "骨质疏松" });
        Assert.DoesNotContain(result, i => i.IssueType == "年龄-诊断矛盾检测");
    }

    // keyword_device: 检查设备-描述矛盾 (rule 4)
    [Fact]
    public void DeviceConflict_CTShowsMRISignal_ReportsError()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { ExamDevice = "CT", Findings = "MRI示异常信号" });
        Assert.Contains(result, i => i.IssueType == "检查设备-描述矛盾" && i.Severity == "error");
    }

    // keyword_scan: 扫描方式-增强描述矛盾 (rule 5)
    [Fact]
    public void ScanEnhanceConflict_PlainScanShowsEnhancement_ReportsError()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { ExamMethod = "平扫", Findings = "病灶明显强化" });
        Assert.Contains(result, i => i.IssueType == "扫描方式-增强描述矛盾");
    }

    [Fact]
    public void ScanEnhanceConflict_EnhancedScan_NoIssue()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { ExamMethod = "增强", Findings = "病灶明显强化" });
        Assert.DoesNotContain(result, i => i.IssueType == "扫描方式-增强描述矛盾");
    }

    // keyword_replace: phrase_typo (rule 6, category=level1_text → IssueType="text_error")
    [Fact]
    public void PhraseTypo_ReportsError()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { Findings = "右肺低密谋灶" });
        Assert.Contains(result, i => i is { SubType: "phrase_typo", OriginalText: "低密谋灶" });
    }

    // direction_compare: 方位-左右矛盾检测 (rule 10)
    [Fact]
    public void DirectionConflict_LeftFindingsRightImpression_ReportsWarning()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { Findings = "左侧结节", Impression = "右侧占位" });
        Assert.Contains(result, i => i.IssueType == "direction_conflict");
    }

    // regex_duplicate: 重复字检测 (rule 11)
    [Fact]
    public void DuplicateChar_ReportsWarning()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { Findings = "检查查所见" });
        Assert.Contains(result, i => i is { SubType: "duplicate_char", OriginalText: "查查" });
    }

    // field_validation: 患者基本信息完整性检查 (rule 13)
    [Fact]
    public void FieldValidation_InvalidGender_ReportsError()
    {
        var engine = CreateEngine();
        var result = engine.Execute(new QcRequest { PatientGender = "未知", ExamPart = "胸部", ExamDevice = "CT" });
        Assert.Contains(result, i => i.SubType == "invalid_field");
    }
}
