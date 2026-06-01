using Agent_QC.Models;

namespace Agent_QC.Services;

public class RuleEngine
{
    private readonly SqliteRuleStore _store;
    private readonly TrieMatcher _trie;
    private readonly RuleExecutor _executor;

    private List<RuleDef> _rules = new();
    private bool _initialized;

    public RuleEngine(string dbPath)
    {
        _store = new SqliteRuleStore(dbPath);
        _trie = new TrieMatcher();
        _executor = new RuleExecutor(_trie);
    }

    /// <summary>From SQLite load all rules, build Trie.</summary>
    public void Initialize()
    {
        if (_initialized) return;
        _rules = _store.LoadAll();
        _trie.Build(_rules);
        _initialized = true;
    }

    /// <summary>Execute all rules, return issue list.</summary>
    public List<QcIssueDto> Execute(QcRequest request)
    {
        if (!_initialized) Initialize();

        var keywordById = _rules.SelectMany(r => r.Keywords)
            .ToDictionary(k => k.Id, k => k);

        return _executor.Execute(request, _rules, keywordById);
    }
}
