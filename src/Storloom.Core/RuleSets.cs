using System.Text.RegularExpressions;

namespace Storloom.Core;

public sealed record RuleSetInfo(string Id, string File, string Name, string Description,
    Dictionary<string, string> Names, Dictionary<string, string> Descriptions, bool DefaultEnabled);
public sealed record RuleSetIndex(int Version, List<RuleSetInfo> Sets);

public static class RuleSets {
    public static RuleSetIndex ReadIndex(string directory) {
        var index = JsonFiles.Read<RuleSetIndex>(Path.Combine(directory, "sets.json"));
        if (index.Version != 1 || index.Sets.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != index.Sets.Count ||
            index.Sets.Any(s => !Regex.IsMatch(s.Id, "^[a-z][a-z0-9-]*$") ||
                !Regex.IsMatch(s.File, "^[a-z][a-z0-9-]*\\.json$")))
            throw new InvalidDataException("Invalid rule-set index.");
        return index;
    }

    public static RuleCatalog Load(string directory, IEnumerable<string>? selected = null, string? local = null) {
        var index = ReadIndex(directory);
        var enabled = new HashSet<string>(selected ?? index.Sets.Where(s => s.DefaultEnabled).Select(s => s.Id),
            StringComparer.OrdinalIgnoreCase);
        if (enabled.Any(id => !index.Sets.Any(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Unknown rule set. Review preferences.");
        var rules = new Dictionary<string, CleanupRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in index.Sets.Where(s => enabled.Contains(s.Id))) {
            var catalog = JsonFiles.Read<RuleCatalog>(Path.Combine(directory, set.File));
            if (catalog.Version != 1) throw new InvalidDataException("Unsupported rule-set version.");
            foreach (var rule in catalog.Rules)
                if (!rules.TryAdd(rule.Id, rule)) throw new InvalidDataException("Duplicate packaged rule: " + rule.Id);
        }
        if (local != null && System.IO.File.Exists(local)) {
            var catalog = JsonFiles.Read<RuleCatalog>(local);
            if (catalog.Version != 1 || catalog.Rules.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Rules.Count)
                throw new InvalidDataException("Invalid local rule overrides.");
            foreach (var rule in catalog.Rules) rules[rule.Id] = rule;
        }
        return new() { Rules = rules.Values.ToList() };
    }
}
