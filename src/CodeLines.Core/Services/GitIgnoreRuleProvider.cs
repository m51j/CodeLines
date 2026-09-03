using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CodeLines.Core.Abstractions;

namespace CodeLines.Core.Services;

public sealed class GitIgnoreRuleProvider : IIgnoreRuleProvider
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<IgnoreRule>> _fileCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<IgnoreRule>> _patternCache =
        new(StringComparer.Ordinal);

    public bool IsIgnored(string projectRoot, string fullPath, bool isDirectory,
        IReadOnlyList<string> globalPatterns, IReadOnlyList<string> projectPatterns)
    {
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(fullPath);
        if (!IsWithinRoot(root, path)) return true;

        var relative = Normalize(Path.GetRelativePath(root, path));
        var ignored = false;
        foreach (var rule in GetRules(root, path, globalPatterns, projectPatterns))
        {
            if (rule.Matches(relative, isDirectory)) ignored = !rule.Negated;
        }
        return ignored;
    }

    private IEnumerable<IgnoreRule> GetRules(string root, string path,
        IReadOnlyList<string> globalPatterns, IReadOnlyList<string> projectPatterns)
    {
        foreach (var rule in GetCachedPatterns(globalPatterns, "global")) yield return rule;

        var parent = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        var directories = new Stack<string>();
        for (var current = parent; IsWithinRoot(root, current); current = Path.GetDirectoryName(current)!)
        {
            directories.Push(current);
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
        }

        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            var ignoreFile = Path.Combine(directory, ".gitignore");
            if (!File.Exists(ignoreFile)) continue;
            var cacheKey = root + "|" + ignoreFile;
            foreach (var rule in _fileCache.GetOrAdd(cacheKey, _ => LoadIgnoreFile(ignoreFile, root))) yield return rule;
        }

        foreach (var rule in GetCachedPatterns(projectPatterns, "project")) yield return rule;
    }

    private IReadOnlyList<IgnoreRule> GetCachedPatterns(IReadOnlyList<string> patterns, string scope)
    {
        var key = scope + '\0' + string.Join('\0', patterns);
        return _patternCache.GetOrAdd(key, _ => ParsePatterns(patterns, string.Empty).ToArray());
    }

    private static IReadOnlyList<IgnoreRule> LoadIgnoreFile(string path, string projectRoot)
    {
        try
        {
            var baseDirectory = Normalize(Path.GetRelativePath(projectRoot, Path.GetDirectoryName(path)!));
            if (baseDirectory == ".") baseDirectory = string.Empty;
            return ParsePatterns(File.ReadAllLines(path), baseDirectory).ToArray();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<IgnoreRule> ParsePatterns(IEnumerable<string> patterns, string baseDirectory)
    {
        foreach (var raw in patterns)
        {
            var value = raw.Trim();
            if (value.Length == 0 || value[0] == '#') continue;
            var negated = value[0] == '!';
            if (negated) value = value[1..];
            if (value.StartsWith("\\#") || value.StartsWith("\\!")) value = value[1..];
            var directoryOnly = value.EndsWith('/');
            value = value.TrimEnd('/');
            if (value.Length == 0) continue;
            yield return IgnoreRule.Create(value, baseDirectory, negated, directoryOnly);
        }
    }

    private static bool IsWithinRoot(string root, string path) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private sealed record IgnoreRule(Regex Regex, bool Negated, bool DirectoryOnly)
    {
        public bool Matches(string relativePath, bool isDirectory) =>
            (!DirectoryOnly || isDirectory || relativePath.Contains('/')) && Regex.IsMatch(relativePath);

        public static IgnoreRule Create(string pattern, string baseDirectory, bool negated, bool directoryOnly)
        {
            pattern = pattern.Replace('\\', '/');
            var anchored = pattern.StartsWith('/');
            pattern = pattern.TrimStart('/');
            var hasSlash = pattern.Contains('/');
            var prefix = string.IsNullOrEmpty(baseDirectory) ? string.Empty : Regex.Escape(baseDirectory.Trim('/')) + "/";
            var body = GlobToRegex(pattern);
            string expression;
            if (anchored || hasSlash)
                expression = "^" + prefix + body + (directoryOnly ? "(?:/.*)?" : string.Empty) + "$";
            else
                expression = "^(?:" + prefix + ".*/)?" + body + (directoryOnly ? "(?:/.*)?" : string.Empty) + "$";
            return new(new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
                negated, directoryOnly);
        }

        private static string GlobToRegex(string pattern)
        {
            var result = new System.Text.StringBuilder();
            for (var i = 0; i < pattern.Length; i++)
            {
                var ch = pattern[i];
                if (ch == '*')
                {
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        while (i + 1 < pattern.Length && pattern[i + 1] == '*') i++;
                        if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                        {
                            i++;
                            result.Append("(?:.*/)?");
                        }
                        else result.Append(".*");
                    }
                    else result.Append("[^/]*");
                }
                else if (ch == '?') result.Append("[^/]");
                else result.Append(Regex.Escape(ch.ToString()));
            }
            return result.ToString();
        }
    }
}
