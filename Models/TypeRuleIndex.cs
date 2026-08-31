namespace ChangExport.Models;

public sealed class TypeRuleIndex
{
    private readonly Dictionary<string, RevitLayerRow[]> _rules;
    private readonly Dictionary<(string Category, string Type), RevitLayerRow?> _matches = new();
    public TypeRuleIndex(IEnumerable<RevitLayerRow> rows) => _rules = rows.Where(r => r.IsCustom)
        .GroupBy(r => r.Category).ToDictionary(g => g.Key, g => g.ToArray());
    public bool HasCategory(string category) => _rules.ContainsKey(category);
    public RevitLayerRow? Match(string category, string typeName)
    {
        var key = (category, typeName);
        if (_matches.TryGetValue(key, out var result)) return result;
        result = _rules.TryGetValue(category, out var rules) ? rules.FirstOrDefault(r => r.Matches(category, typeName)) : null;
        _matches[key] = result; return result;
    }
}
