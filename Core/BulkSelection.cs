namespace SimilarVideoFinder.Core;

public enum KeepCopyPolicy { LargestFile, SmallestFile, FirstResult, PreferredFolder, HighestResolution, PreferredCodec, PreferredDynamicRange, PreferredAudioLanguage, MostSubtitles }
public sealed record KeepPreferences(string Folder = "", string Codec = "hevc", string DynamicRange = "HDR", string AudioLanguage = "eng");
public sealed record BulkSelectionResult(HashSet<string> SelectedPaths, int Groups, int CopiesKept);

public static class BulkSelection
{
    public static BulkSelectionResult Create(ScanReport report, IReadOnlySet<string> eligiblePaths, KeepCopyPolicy policy, KeepPreferences? preferences = null)
    {
        var eligible = new HashSet<string>(eligiblePaths, StringComparer.OrdinalIgnoreCase);
        eligible.IntersectWith(ReferencePolicy.EligiblePaths(report));
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keepers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int groups = 0;
        foreach (var group in report.Groups)
        {
            var members = group.Members.Select(m => m.File).Where(f => eligible.Contains(f.Path))
                .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            bool hasReference = group.Members.Any(m => ReferencePolicy.Protected(report, m.File));
            if (members.Length < (hasReference ? 1 : 2)) continue;
            groups++;
            foreach (var file in members) candidates.Add(file.Path);
            if (hasReference) foreach (var member in group.Members.Where(m => ReferencePolicy.Protected(report, m.File))) keepers.Add(member.File.Path);
            else keepers.Add(ChooseKeeper(members, policy, preferences).Path);
        }
        var selected = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
        selected.ExceptWith(keepers);
        // A path can occur in other search groups, including groups outside the current filter.
        // Protect every group, rather than treating each row's checkbox independently.
        foreach (var group in report.Groups)
            if (group.Members.Count > 0 && group.Members.All(m => selected.Contains(m.File.Path)))
                selected.Remove(ChooseKeeper(group.Members.Select(m => m.File), policy, preferences).Path);
        // Retain additional witnesses where fuzzy matches are not transitive.
        foreach (var group in report.Groups)
            foreach (var member in group.Members.Where(m => selected.Contains(m.File.Path)).ToArray())
                if (!group.Members.Any(m => !selected.Contains(m.File.Path) && ReferencePolicy.Compare(group, member.File, m.File, report) != null))
                    selected.Remove(member.File.Path);
        return new(selected, groups, candidates.Count - selected.Count + keepers.Count(p => !candidates.Contains(p)));
    }
    private static FileRecord ChooseKeeper(IEnumerable<FileRecord> files, KeepCopyPolicy policy, KeepPreferences? preferences = null)
    {
        preferences ??= new();
        double Rank(FileRecord f) => policy switch
        {
            KeepCopyPolicy.PreferredFolder => !string.IsNullOrWhiteSpace(preferences.Folder) && ReferencePolicy.IsWithin(f.Path, preferences.Folder) ? 1 : 0,
            KeepCopyPolicy.HighestResolution => (double)(f.Media?.Width ?? 0) * (f.Media?.Height ?? 0),
            KeepCopyPolicy.PreferredCodec => f.Media?.VideoCodec.Equals(preferences.Codec, StringComparison.OrdinalIgnoreCase) == true ? 1 : 0,
            KeepCopyPolicy.PreferredDynamicRange => f.Media?.DynamicRange.Contains(preferences.DynamicRange, StringComparison.OrdinalIgnoreCase) == true ? 1 : 0,
            KeepCopyPolicy.PreferredAudioLanguage => f.Media?.AudioTracks.Any(t => t.Language.Equals(preferences.AudioLanguage, StringComparison.OrdinalIgnoreCase)) == true ? 1 : 0,
            KeepCopyPolicy.MostSubtitles => f.Media?.SubtitleTracks.Count ?? 0,
            _ => 0
        };
        using var enumerator = files.GetEnumerator();
        if (!enumerator.MoveNext()) throw new ArgumentException("A group needs at least one file.");
        var keeper = enumerator.Current;
        if (policy == KeepCopyPolicy.FirstResult) return keeper;
        while (enumerator.MoveNext())
        {
            var candidate = enumerator.Current;
            if (policy > KeepCopyPolicy.FirstResult)
            {
                int rank = Rank(candidate).CompareTo(Rank(keeper));
                if (rank > 0 || rank == 0 && candidate.Size > keeper.Size) keeper = candidate;
                continue;
            }
            int sizeOrder = candidate.Size.CompareTo(keeper.Size);
            if ((policy == KeepCopyPolicy.LargestFile && sizeOrder > 0) || (policy == KeepCopyPolicy.SmallestFile && sizeOrder < 0)
                || sizeOrder == 0 && StringComparer.OrdinalIgnoreCase.Compare(candidate.Path, keeper.Path) < 0) keeper = candidate;
        }
        return keeper;
    }
}
