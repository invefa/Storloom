using System.Text;
using System.Text.RegularExpressions;

namespace Storloom.Core;

public static class Glob {
    public static Regex Compile(string pattern) {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 512 ||
            pattern.Contains(':') || pattern.StartsWith('/') || pattern.StartsWith('\\') ||
            pattern.Replace('\\', '/').Split('/').Any(p => p == "..")) {
            throw new InvalidDataException($"Invalid relative glob: {pattern}");
        }
        pattern = pattern.Replace('\\', '/');
        var result = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++) {
            char c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*') {
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == '/') {
                    result.Append("(?:.*/)?");
                    i++;
                } else result.Append(".*");
            } else if (c == '*') result.Append("[^/]*");
            else if (c == '?') result.Append("[^/]");
            else result.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(result.Append('$').ToString(), RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    }

    public static bool IsMatch(string path, string pattern) =>
        Compile(pattern).IsMatch(path.Replace('\\', '/'));
}
