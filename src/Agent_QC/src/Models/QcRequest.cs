namespace Agent_QC.Models;

public class QcRequest
{
    public string ReportId { get; set; } = string.Empty;
    public string Findings { get; set; } = string.Empty;
    public string Impression { get; set; } = string.Empty;
    public string? ReportType { get; set; }
    public string? PatientName { get; set; }
    public string? PatientGender { get; set; }
    public int? PatientAge { get; set; }
    public string? ClinicalDiagnosis { get; set; }
    public string? RequestDepartment { get; set; }
    public string? ExamPart { get; set; }
    public string? ExamMethod { get; set; }
    public string? ExamDevice { get; set; }
    public string? AccessionNo { get; set; }

    /// <summary>影像所见分词后结果（Level 0 预处理阶段填充）</summary>
    public List<string>? SegmentedFindings { get; set; }

    /// <summary>影像结论分词后结果（Level 0 预处理阶段填充）</summary>
    public List<string>? SegmentedImpression { get; set; }
}
