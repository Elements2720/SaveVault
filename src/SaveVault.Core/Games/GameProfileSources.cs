namespace SaveVault.Core.Games;

public static class GameProfileSources
{
    public static List<SourceFolder> Merge(IEnumerable<SaveCandidate> confirmed, IEnumerable<SourceFolder>? existing = null)
    {
        var sources = existing?.ToList() ?? [];
        foreach (var candidate in confirmed)
        {
            var path = PathSafety.Full(candidate.Root);
            foreach (var include in candidate.Includes) SourceSelection.Validate(include);
            var parent = sources.FirstOrDefault(source => PathSafety.Contains(source.Path, path));
            if (parent is not null)
            {
                if (parent.Includes.Count == 0) continue;
                var relative = Path.GetRelativePath(parent.Path, path).Replace('\\', '/');
                var additions = candidate.Includes.Count == 0 ? (relative == "." ? new List<string>() : [relative])
                    : candidate.Includes.Select(include => relative == "." ? include : relative + "/" + include).ToList();
                var includes = relative == "." && candidate.Includes.Count == 0 ? []
                    : parent.Includes.Concat(additions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                sources[sources.IndexOf(parent)] = parent with { Includes = includes };
                continue;
            }
            var children = sources.Where(source => PathSafety.Contains(path, source.Path)).ToList();
            var selection = candidate.Includes.ToList();
            if (selection.Count > 0)
                foreach (var child in children)
                {
                    var relative = Path.GetRelativePath(path, child.Path).Replace('\\', '/');
                    selection.AddRange(child.Includes.Count == 0 ? [relative] : child.Includes.Select(include => relative + "/" + include));
                }
            foreach (var child in children) sources.Remove(child);
            sources.Add(new SourceFolder { Name = Path.GetFileName(path) is { Length: > 0 } name ? name : "Drive", Path = path,
                Includes = selection.Distinct(StringComparer.OrdinalIgnoreCase).ToList() });
        }
        return sources;
    }
}
