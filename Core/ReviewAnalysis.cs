using System.Security.Cryptography;
using System.Text;

namespace SimilarVideoFinder.Core;

public sealed record PairEvidence(string Left, string Right, string Search, string Reason, double Score);
public static class ReviewAnalysis
{
    public static List<PairEvidence> Evidence(ScanReport report, FileRecord a, FileRecord b) => report.Groups
        .Where(g => g.Members.Any(m => m.File.Path.Equals(a.Path, StringComparison.OrdinalIgnoreCase)) && g.Members.Any(m => m.File.Path.Equals(b.Path, StringComparison.OrdinalIgnoreCase)))
        .Select(g => (Group: g, Evidence: ReferencePolicy.Compare(g, a, b, report))).Where(x => x.Evidence != null)
        .DistinctBy(x => x.Group.Kind).Select(x => new PairEvidence(a.Path, b.Path, x.Group.Label, x.Evidence!.Reason, x.Evidence.Score)).ToList();

    public static List<string> LossWarnings(FileRecord removed, IEnumerable<FileRecord> retained)
    {
        var copies = retained.ToArray();
        if (copies.Length == 0) return ["No copy will remain for this selection."];
        if (removed.Media == null || copies.Any(f => f.Media == null)) return ["Track information is incomplete; inspect the videos before deleting."];
        var source = removed.Media;
        var warnings = new List<string>();
        string Key(MediaTrack t) => $"{t.Language}|{t.Title}|{t.Channels}".ToLowerInvariant();
        foreach (var track in source.AudioTracks)
            if (!copies.Any(f => f.Media!.AudioTracks.Any(t => Key(t) == Key(track)))) warnings.Add($"Audio may be lost: {track.Language} {track.Title} ({track.Channels} channels)");
        foreach (var track in source.SubtitleTracks)
            if (!copies.Any(f => f.Media!.SubtitleTracks.Any(t => Key(t) == Key(track)))) warnings.Add($"Subtitle may be lost: {track.Language} {track.Title}");
        if (source.DynamicRange != "Unknown" && !copies.Any(f => f.Media!.DynamicRange == source.DynamicRange)) warnings.Add("Dynamic range version may be lost: " + source.DynamicRange);
        return warnings;
    }
    public static List<FileRecord> Retained(ScanReport report, FileRecord file, IReadOnlySet<string> selected) => report.Groups
        .Where(g => g.Members.Any(m => m.File.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)))
        .SelectMany(g => g.Members.Where(m => !selected.Contains(m.File.Path) && ReferencePolicy.Compare(g, file, m.File, report) != null).Select(m => m.File))
        .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    public static string PairKey(FileRecord a, FileRecord b)
    {
        string Identity(FileRecord f) => $"{f.Path.ToUpperInvariant()}|{f.Size}|{f.ModifiedTicks}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", new[] { Identity(a), Identity(b) }.Order(StringComparer.Ordinal)))));
    }
}
