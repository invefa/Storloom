using System.Text.RegularExpressions;

namespace Storloom.Core;

public sealed record RuleSetInfo(string Id, string File, string Name, string Description,
    Dictionary<string, string> Names, Dictionary<string, string> Descriptions, bool DefaultEnabled) {
    [System.Text.Json.Serialization.JsonIgnore] public bool IsCustom { get; init; }
}
public sealed record RuleSetIndex(int Version, List<RuleSetInfo> Sets);

public static class RuleSets {
    public static RuleSetIndex ReadIndex(string directory, string? customDirectory = null) {
        var index = JsonFiles.Read<RuleSetIndex>(Path.Combine(directory, "sets.json"));
        if (index.Version != 1 || index.Sets.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != index.Sets.Count ||
            index.Sets.Any(s => !Regex.IsMatch(s.Id, "^[a-z][a-z0-9-]*$") ||
                !Regex.IsMatch(s.File, "^[a-z][a-z0-9-]*\\.json$")))
            throw new InvalidDataException("Invalid rule-set index.");
        if (customDirectory != null && System.IO.File.Exists(Path.Combine(customDirectory, "sets.json"))) {
            var custom = ReadIndex(customDirectory).Sets.Select(s => s with { IsCustom = true }).ToList();
            if (custom.Any(c => index.Sets.Any(s => s.Id.Equals(c.Id, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("Imported rule-set IDs must be distinct from packaged rule sets.");
            index = index with { Sets = [.. index.Sets, .. custom] };
        }
        return index;
    }

    public static RuleCatalog Load(string directory, IEnumerable<string>? selected = null, string? local = null, string? customDirectory = null) {
        var index = ReadIndex(directory, customDirectory);
        var enabled = new HashSet<string>(selected ?? index.Sets.Where(s => s.DefaultEnabled).Select(s => s.Id),
            StringComparer.OrdinalIgnoreCase);
        if (enabled.Any(id => !index.Sets.Any(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Unknown rule set. Review preferences.");
        var catalogs = index.Sets.Where(s => enabled.Contains(s.Id)).Select(set =>
            JsonFiles.Read<RuleCatalog>(Path.Combine(set.IsCustom ? customDirectory! : directory, set.File)));
        return Combine(catalogs, local != null && System.IO.File.Exists(local) ? JsonFiles.Read<RuleCatalog>(local) : null);
    }
    public static RuleCatalog Combine(IEnumerable<RuleCatalog> catalogs, RuleCatalog? overrides = null) {
        var rules = new Dictionary<string, CleanupRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var catalog in catalogs) {
            if (catalog.Version != 1) throw new InvalidDataException("Unsupported rule-set version.");
            foreach (var rule in catalog.Rules)
                if (!rules.TryAdd(rule.Id, rule)) throw new InvalidDataException("Duplicate packaged rule: " + rule.Id);
        }
        if (overrides != null) {
            var catalog = overrides;
            if (catalog.Version != 1 || catalog.Rules.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Rules.Count)
                throw new InvalidDataException("Invalid local rule overrides.");
            foreach (var rule in catalog.Rules) rules[rule.Id] = rule;
        }
        return new() { Rules = rules.Values.ToList() };
    }
}
