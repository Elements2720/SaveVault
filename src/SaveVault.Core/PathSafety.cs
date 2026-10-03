namespace SaveVault.Core;

internal static class PathSafety
{
    internal static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal static StringComparer Comparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static bool Contains(string parent, string child)
    {
        parent = Full(parent);
        child = Full(child);
        return child.Equals(parent, Comparison) || child.StartsWith(
            Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, Comparison);
    }

    internal static void RejectLinks(string path)
    {
        for (var current = Full(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            // File.GetAttributes also detects dangling symbolic links on supported filesystems.
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Symbolic links and junctions are not supported: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static string SafeRelative(string value)
    {
        var normalized = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized)
            || normalized.Split('/').Any(part => part is "" or "." or ".."
                || part.Contains(':') || part.Any(c => c < 32 || "<>\"|?*".Contains(c))
                || part.EndsWith(' ') || part.EndsWith('.')))
            throw new InvalidDataException($"Unsafe relative path in snapshot: {value}");
        // Device names remain reserved on Windows even with file extensions.
        foreach (var part in normalized.Split('/'))
        {
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL"
                || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT"))
                    && stem[3] is >= '1' and <= '9'))
                throw new InvalidDataException($"Reserved Windows name in snapshot: {value}");
        }
        return normalized.Replace('/', Path.DirectorySeparatorChar);
    }

    internal static string Resolve(string root, string relative)
    {
        var result = Full(Path.Combine(root, SafeRelative(relative)));
        if (!Contains(root, result)) throw new InvalidDataException("Path escapes its destination.");
        RejectLinks(result);
        return result;
    }
}
