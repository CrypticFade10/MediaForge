namespace SimilarVideoFinder.Core;

public static class ReferencePolicy
{
    public static bool IsWithin(string path, string root)
    {
        string folder = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/');
        string full = System.IO.Path.GetFullPath(path);
        return full.Equals(folder, StringComparison.OrdinalIgnoreCase) || full.StartsWith(folder + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    public static bool Protected(ScanReport report, FileRecord file) => file.IsReference || report.ReferenceRoots.Any(r => IsWithin(file.Path, r));
    public static MatchEvidence? Compare(MatchGroup group, FileRecord a, FileRecord b, ScanReport report) => group.Kind switch
    {
        MatchKind.SimilarFilename => FilenameMatcher.Compare(FilenameMatcher.Parse(a.Name), FilenameMatcher.Parse(b.Name), report.NameThreshold),
        MatchKind.VideoContent => VideoMatcher.Compare(a, b, report.FrameDistance),
        // Exact groups already contain direct equality evidence for all their members.
        _ => new(100, "Direct match in " + group.Label)
    };
    public static HashSet<string> EligiblePaths(ScanReport report)
    {
        var eligible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool referencesRequired = report.ReferenceRoots.Count > 0 || report.Groups.Any(g => g.Members.Any(m => m.File.IsReference));
        foreach (var group in report.Groups)
        {
            var references = referencesRequired ? group.Members.Where(m => Protected(report, m.File)).Select(m => m.File).ToArray() : [];
            foreach (var member in group.Members)
                if (!Protected(report, member.File) && (!referencesRequired || references.Any(r => Compare(group, r, member.File, report) != null)))
                    eligible.Add(member.File.Path);
        }
        return eligible;
    }
    public static bool HasCurrentReference(ScanReport report, FileRecord file, IEnumerable<MatchGroup> groups) =>
        report.ReferenceRoots.Count == 0 && !report.Groups.Any(g => g.Members.Any(m => m.File.IsReference)) ||
        groups.Any(g => g.Members.Any(m => Protected(report, m.File) && Compare(g, m.File, file, report) != null && m.File.IsCurrent()));
}
