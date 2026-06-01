namespace Agent_QC.Models;

/// <summary>Named entity extracted from report text.</summary>
public record NerEntity
{
    /// <summary>"anatomy" | "direction" | "finding" | "measure"</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Original text span.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Canonical form after normalization.</summary>
    public string Normalized { get; init; } = string.Empty;

    public int Start { get; init; }
    public int End { get; init; }

    /// <summary>Confidence score 0.0–1.0.</summary>
    public float Confidence { get; init; }
}
