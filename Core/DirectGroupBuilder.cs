namespace SimilarVideoFinder.Core;

// Overlapping stars preserve every discovered direct edge without implying transitivity.
internal sealed class DirectGroupBuilder(MatchKind kind)
{
    private readonly Dictionary<FileRecord, HashSet<MatchGroup>> membership = [];
    private readonly Dictionary<FileRecord, MatchGroup> anchors = [];
    public List<MatchGroup> Groups { get; } = [];
    public void Add(FileRecord a, FileRecord b, MatchEvidence evidence)
    {
        if (!membership.TryGetValue(a, out var left)) membership[a] = left = [];
        if (!membership.TryGetValue(b, out var right)) membership[b] = right = [];
        if (left.Any(right.Contains)) return;
        bool Compatible(MatchGroup group)
        {
            if (kind != MatchKind.SimilarFilename) return true;
            string? year = FilenameMatcher.Parse(b.Name).Year;
            return year == null || group.Members.All(m => FilenameMatcher.Parse(m.File.Name).Year is not { } existing || existing == year);
        }
        if (!anchors.TryGetValue(a, out var group) || !Compatible(group))
        {
            anchors[a] = group = new() { Kind = kind, Members = [new(a, new(100, "Reference file"))] };
            Groups.Add(group); left.Add(group);
        }
        group.Members.Add(new(b, evidence)); right.Add(group);
    }
}
