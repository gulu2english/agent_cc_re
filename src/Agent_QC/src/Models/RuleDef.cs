namespace Agent_QC.Models;

/// <summary>规则定义（从 SQLite rule_def 表加载）。</summary>
public record RuleDef
{
    public int Id { get; init; }
    public string RuleType { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Severity { get; init; } = "warning";
    public string? ParamsJson { get; init; }
    public string? Description { get; init; }
    public bool IsActive { get; init; } = true;
    public int SortOrder { get; init; }

    /// <summary>关联的关键词列表。</summary>
    public List<RuleKeyword> Keywords { get; init; } = new();
}

/// <summary>规则关键词（从 SQLite rule_keyword 表加载）。</summary>
public record RuleKeyword
{
    public int Id { get; init; }
    public int RuleId { get; init; }
    public string Keyword { get; init; } = string.Empty;
    public int KeywordLen { get; init; }
    public int Priority { get; init; }
    public bool IsExclude { get; init; }
    public string? ExtraData { get; init; }
}
