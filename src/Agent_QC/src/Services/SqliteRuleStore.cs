using Microsoft.Data.Sqlite;
using Agent_QC.Models;

namespace Agent_QC.Services;

public class SqliteRuleStore
{
    private readonly string _dbPath;

    public SqliteRuleStore(string dbPath) => _dbPath = dbPath;

    public List<RuleDef> LoadAll()
    {
        var rules = new List<RuleDef>();
        var keywords = new Dictionary<int, List<RuleKeyword>>();

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        // Load keywords first
        using var kwCmd = new SqliteCommand(
            "SELECT id, rule_id, keyword, keyword_len, priority, is_exclude, extra_data FROM rule_keyword ORDER BY keyword_len DESC", conn);
        using var kwReader = kwCmd.ExecuteReader();
        while (kwReader.Read())
        {
            var kw = new RuleKeyword
            {
                Id = kwReader.GetInt32(0),
                RuleId = kwReader.GetInt32(1),
                Keyword = kwReader.GetString(2),
                KeywordLen = kwReader.GetInt32(3),
                Priority = kwReader.GetInt32(4),
                IsExclude = kwReader.GetBoolean(5),
                ExtraData = kwReader.IsDBNull(6) ? null : kwReader.GetString(6),
            };
            if (!keywords.ContainsKey(kw.RuleId))
                keywords[kw.RuleId] = new List<RuleKeyword>();
            keywords[kw.RuleId].Add(kw);
        }

        // Load rule definitions
        using var ruleCmd = new SqliteCommand(
            "SELECT id, rule_type, name, category, severity, params_json, description, is_active, sort_order FROM rule_def WHERE is_active = 1 ORDER BY sort_order", conn);
        using var ruleReader = ruleCmd.ExecuteReader();
        while (ruleReader.Read())
        {
            var rule = new RuleDef
            {
                Id = ruleReader.GetInt32(0),
                RuleType = ruleReader.GetString(1),
                Name = ruleReader.GetString(2),
                Category = ruleReader.GetString(3),
                Severity = ruleReader.GetString(4),
                ParamsJson = ruleReader.IsDBNull(5) ? null : ruleReader.GetString(5),
                Description = ruleReader.IsDBNull(6) ? null : ruleReader.GetString(6),
                IsActive = ruleReader.GetBoolean(7),
                SortOrder = ruleReader.GetInt32(8),
                Keywords = keywords.GetValueOrDefault(ruleReader.GetInt32(0), new List<RuleKeyword>()),
            };
            rules.Add(rule);
        }

        return rules;
    }
}
