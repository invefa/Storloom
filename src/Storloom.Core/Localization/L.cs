using System.Globalization;
using System.Text.Json;

namespace Storloom.Core.Localization;

public static class L {
    private static readonly Dictionary<string, Dictionary<string, string>> catalogs = Load();
    public static string Language { get; private set; } =
        CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";
    public static IReadOnlyCollection<string> Keys => catalogs["en"].Keys;

    private static Dictionary<string, Dictionary<string, string>> Load() {
        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (string language in new[] { "en", "zh-CN" }) {
            using var input = typeof(L).Assembly.GetManifestResourceStream(
                $"Storloom.Core.Localization.Locales.{language}.json") ??
                throw new InvalidDataException("Missing language resource.");
            result[language] = JsonSerializer.Deserialize<Dictionary<string, string>>(input)!;
        }
        return result;
    }

    public static void SetLanguage(string language) {
        Language = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(Language);
    }

    public static string T(string key, params object[] arguments) {
        string value = catalogs[Language].GetValueOrDefault(key, catalogs["en"].GetValueOrDefault(key, key));
        return arguments.Length == 0 ? value : string.Format(CultureInfo.GetCultureInfo(Language), value, arguments);
    }

    public static string RuleName(CleanupRule rule) => rule.Names.GetValueOrDefault(Language,
        catalogs[Language].GetValueOrDefault($"rule.{rule.Id}.name", rule.Name));
    public static string RuleDescription(CleanupRule rule) => rule.Descriptions.GetValueOrDefault(Language,
        catalogs[Language].GetValueOrDefault($"rule.{rule.Id}.description", rule.Description));
}
