namespace SimilarVideoFinder.Core;

public static class EpisodeFolderMatcher
{
    public static List<MatchGroup> Find(IEnumerable<FileRecord> files, CancellationToken cancellation = default)
    {
        var groups = new Dictionary<string, MatchGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancellation.ThrowIfCancellationRequested();
            var episode = FilenameMatcher.Parse(file.Name).Episode;
            if (episode == null) continue;
            // Each immediate containing directory is its own scope, including when roots overlap.
            string directory = System.IO.Path.GetDirectoryName(file.Path) ?? "";
            string key = directory + '\0' + episode;
            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = new() { Kind = MatchKind.EpisodeInFolder };
            group.Members.Add(new(file, new(100, $"Same season/episode ({episode}) in the same containing folder; contents may differ")));
        }
        return groups.Values.Where(g => g.Members.Count > 1).ToList();
    }
}
