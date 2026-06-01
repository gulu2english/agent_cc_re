using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Agent_QC.Models;

namespace Agent_QC.Services;

/// <summary>Maps entity synonyms to canonical forms and deduplicates overlapping spans.</summary>
public class EntityNormalizer
{
    private readonly Dictionary<string, string> _synonymToCanonical = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _canonicalToCategory = new(StringComparer.Ordinal);

    public EntityNormalizer(string terminologyPath)
    {
        if (!File.Exists(terminologyPath)) return;

        var yaml = File.ReadAllText(terminologyPath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var raw = deserializer.Deserialize<List<TerminologyEntry>>(yaml);
        if (raw == null) return;

        foreach (var entry in raw)
        {
            if (string.IsNullOrWhiteSpace(entry.StandardTerm)) continue;
            var canonical = entry.StandardTerm;
            _canonicalToCategory[canonical] = entry.Category ?? "";

            if (entry.NonStandardTerms != null)
            {
                foreach (var ns in entry.NonStandardTerms)
                {
                    if (!string.IsNullOrWhiteSpace(ns))
                        _synonymToCanonical[ns] = canonical;
                }
            }
        }
    }

    /// <summary>Normalize synonyms to canonical forms. Deduplicate overlapping spans (longest wins).</summary>
    public List<NerEntity> Normalize(List<NerEntity> entities)
    {
        if (entities == null || entities.Count == 0) return new List<NerEntity>();

        var normalized = new List<NerEntity>(entities.Count);

        foreach (var e in entities)
        {
            var canonical = e.Text;
            if (_synonymToCanonical.TryGetValue(e.Text, out var mapped))
                canonical = mapped;

            normalized.Add(e with { Normalized = canonical });
        }

        return Deduplicate(normalized);
    }

    /// <summary>Remove overlapping entity spans, keeping the one with higher confidence (then longer text).</summary>
    private static List<NerEntity> Deduplicate(List<NerEntity> entities)
    {
        if (entities.Count <= 1) return entities;

        var sorted = entities
            .OrderBy(e => e.Start)
            .ThenByDescending(e => e.End - e.Start)
            .ToList();

        var result = new List<NerEntity> { sorted[0] };
        var lastEnd = sorted[0].End;

        for (int i = 1; i < sorted.Count; i++)
        {
            var e = sorted[i];
            if (e.Start < lastEnd)
            {
                // Overlapping — skip shorter / lower-confidence span
                var prev = result[^1];
                if (e.Confidence > prev.Confidence || (e.Confidence == prev.Confidence && (e.End - e.Start) > (prev.End - prev.Start)))
                {
                    result[^1] = e;
                    lastEnd = e.End;
                }
            }
            else
            {
                result.Add(e);
                lastEnd = e.End;
            }
        }

        return result;
    }

    private class TerminologyEntry
    {
        public string? Category { get; set; }
        public List<string>? NonStandardTerms { get; set; }
        public string? StandardTerm { get; set; }
    }
}
