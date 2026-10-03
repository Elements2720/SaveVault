using System.IO.Enumeration;

namespace SaveVault.Core;

internal static class SourceSelection
{
    internal static void Validate(string pattern)
    {
        if (pattern.Contains("**", StringComparison.Ordinal))
            throw new ArgumentException("Source includes use component wildcards, not recursive ** patterns.");
        PathSafety.SafeRelative(pattern.Replace('*', 'x').Replace('?', 'x'));
    }

    internal static bool Matches(string relative, bool isDirectory, IReadOnlyList<string> includes)
    {
        if (includes.Count == 0) return true;
        var parts = relative.Replace('\\', '/').Split('/');
        foreach (var include in includes)
        {
            var pattern = include.Replace('\\', '/').Split('/');
            var matches = true;
            for (var i = 0; i < Math.Min(parts.Length, pattern.Length); i++)
                if (!FileSystemName.MatchesSimpleExpression(pattern[i], parts[i], ignoreCase: true))
                { matches = false; break; }
            if (matches && (parts.Length >= pattern.Length || isDirectory)) return true;
        }
        return false;
    }
}
