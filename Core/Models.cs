using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SimilarVideoFinder.Core;

public enum MatchKind { IdenticalFilename, IdenticalContents, SimilarFilename, VideoContent, EpisodeInFolder }
public enum ScanStatus { Completed, Cancelled, Failed }

public sealed class ScanOptions
{
    public List<string> Roots { get; set; } = [];
    public List<string> ReferenceRoots { get; set; } = [];
    public bool Recursive { get; set; } = true;
    public string Extensions { get; set; } = ".mkv .mp4 .avi .m4v .mov .wmv .ts .m2ts .mpg .mpeg .webm .vob .flv .3gp";
    public bool IdenticalFilename { get; set; } = true;
    public bool IdenticalContents { get; set; } = true;
    public bool SimilarFilename { get; set; } = true;
    public bool VideoContent { get; set; }
    public bool EpisodeInFolder { get; set; } = true;
    public bool ForceRescan { get; set; }
    public int NameThreshold { get; set; } = 90;
    public int FrameDistance { get; set; } = 10;
    public int Workers { get; set; } = 1;
    public List<string> ExcludedFolders { get; set; } = [];
    public string ExcludedPatterns { get; set; } = "";
    public long MinimumSizeBytes { get; set; }
    public double MinimumDurationSeconds { get; set; }
    public KeepPreferences KeepPreferences { get; set; } = new();
    public void Validate()
    {
        if (Roots.Count == 0 && ReferenceRoots.Count == 0) throw new ArgumentException("Select at least one folder.");
        if (!IdenticalFilename && !IdenticalContents && !SimilarFilename && !VideoContent && !EpisodeInFolder)
            throw new ArgumentException("Choose at least one search.");
        if (NameThreshold is < 50 or > 100 || FrameDistance is < 1 or > 20 || Workers is < 1 or > 4)
            throw new ArgumentException("Check the advanced settings: name threshold 50-100, visual distance 1-20, workers 1-4.");
        if (string.IsNullOrWhiteSpace(Extensions)) throw new ArgumentException("Enter at least one video extension.");
        if (MinimumSizeBytes < 0 || !double.IsFinite(MinimumDurationSeconds) || MinimumDurationSeconds < 0) throw new ArgumentException("Minimum size and duration must be non-negative.");
    }
}

public sealed class FileRecord
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public long ModifiedTicks { get; set; }
    public string? SampleHash { get; set; }
    public string? FullHash { get; set; }
    public MediaInfo? Media { get; set; }
    public List<FrameFingerprint>? Frames { get; set; }
    public bool IsReference { get; set; }
    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path);
    public bool IsCurrent()
    {
        try { var f = new FileInfo(Path); return f.Exists && f.Length == Size && f.LastWriteTimeUtc.Ticks == ModifiedTicks
            && (f.Attributes & FileAttributes.ReparsePoint) == 0; }
        catch { return false; }
    }
}

public sealed record MediaTrack(string Codec, string Language, string Title, int Channels = 0, bool Default = false);
public sealed record MediaInfo(double Duration, int Width, int Height, string VideoCodec, string AudioCodec)
{
    public List<MediaTrack> AudioTracks { get; init; } = [];
    public List<MediaTrack> SubtitleTracks { get; init; } = [];
    public int BitDepth { get; init; }
    public string DynamicRange { get; init; } = "Unknown";
    public long Bitrate { get; init; }
    public string PixelFormat { get; init; } = "";
    [JsonIgnore] public string Description => $"{Width} � {Height} � {VideoCodec} � {BitDepth switch { > 0 => $"{BitDepth}-bit", _ => "bit depth unknown" }} � {DynamicRange}" +
        (Bitrate > 0 ? $" � {Bitrate / 1_000_000d:0.##} Mb/s" : "") +
        "\nAudio: " + (AudioTracks.Count == 0 ? AudioCodec : string.Join("; ", AudioTracks.Select(DescribeTrack))) +
        "\nSubtitles: " + (SubtitleTracks.Count == 0 ? "none" : string.Join("; ", SubtitleTracks.Select(DescribeTrack)));
    private static string DescribeTrack(MediaTrack t) => $"{t.Codec} [{t.Language}]" + (t.Channels > 0 ? $" {t.Channels} channels" : "") +
        (string.IsNullOrWhiteSpace(t.Title) ? "" : $" ({t.Title})") + (t.Default ? " � default" : "");
}
public sealed record FrameFingerprint(int Index, ulong Hash, bool Informative);
public sealed record MatchEvidence(double Score, string Reason, List<int>? FrameDistances = null);
public sealed record MatchMember(FileRecord File, MatchEvidence Evidence);

public sealed class MatchGroup
{
    public string Id { get; set; } = "";
    public MatchKind Kind { get; set; }
    public List<MatchMember> Members { get; set; } = [];
    [JsonIgnore] public FileRecord Representative => Members[0].File;
    [JsonIgnore] public string Label => Kind switch
    {
        MatchKind.IdenticalFilename => "Identical filename � candidate",
        MatchKind.IdenticalContents => "Identical contents � SHA-256",
        MatchKind.SimilarFilename => "Similar filename � candidate",
        MatchKind.EpisodeInFolder => "Same episode in folder � candidate",
        _ => "Video content � candidate"
    };
    [JsonIgnore] public string Summary => $"{Id} � {Label} � {Members.Count:N0} files � {Formatting.Size(Members.Sum(m => m.File.Size))}";
}

public sealed record ScanProgress(string Stage, int Completed, int Total, long BytesRead = 0, string Detail = "", double ElapsedSeconds = 0, int CacheHits = 0, double? RemainingSeconds = null);
public sealed class ScanReport
{
    public long ScanId { get; set; }
    public int FileCount { get; set; }
    public List<MatchGroup> Groups { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public bool Cancelled { get; set; }
    public ScanStatus Status { get; set; }
    public List<string> ReferenceRoots { get; set; } = [];
    public int NameThreshold { get; set; } = 90;
    public int FrameDistance { get; set; } = 10;
    public List<DeleteOutcome> DeletionOutcomes { get; set; } = [];
}

public sealed class ResultRow : INotifyPropertyChanged
{
    public required MatchGroup Group { get; init; }
    public required MatchMember Member { get; init; }
    public bool ReferenceEligible { get; init; } = true;
    public bool IsGroupHeader { get; init; }
    private bool collapsed;
    public bool Collapsed { get => collapsed; set { collapsed = value; Changed(); Changed(nameof(HeaderGlyph)); } }
    public string HeaderGlyph => Collapsed ? "?" : "?";
    private bool selected;
    private string state = "";
    public string GroupId => Group.Id;
    public string MatchType => Group.Label;
    public string Filename => Member.File.Name;
    public string Path => Member.File.Path;
    public string Size => Formatting.Size(Member.File.Size);
    public string Score => Group.Kind == MatchKind.IdenticalContents ? "Confirmed" : Member.Evidence.Reason.StartsWith("Reference", StringComparison.Ordinal) ? "Reference" :
        Group.Kind is MatchKind.IdenticalFilename or MatchKind.EpisodeInFolder ? "Candidate" : $"{Member.Evidence.Score:0.0}%";
    public long SizeBytes => Member.File.Size;
    public double ScoreValue => Member.Evidence.Score;
    public double DurationSeconds => Member.File.Media?.Duration ?? -1;
    public long ResolutionPixels => (long)(Member.File.Media?.Width ?? 0) * (Member.File.Media?.Height ?? 0);
    private string headerSelection = " � 0 selected � 0 B";
    public string HeaderSelection { get => headerSelection; set { if (headerSelection == value) return; headerSelection = value; Changed(nameof(GroupSummary)); } }
    public string GroupSummary => Group.Summary + headerSelection;
    public string Protection => Member.File.IsReference ? "Protected reference" : ReferenceEligible ? "" : "No reference match";
    public string Reason => Member.Evidence.Reason;
    public string Duration => Member.File.Media is { } m ? Formatting.Duration(m.Duration) : "-";
    public string Resolution => Member.File.Media is { } m ? $"{m.Width} � {m.Height}" : "-";
    public string Codecs => Member.File.Media is { } m ? $"{m.VideoCodec} / {m.AudioCodec}" : "-";
    public bool CanSelect => !IsGroupHeader && !Member.File.IsReference && ReferenceEligible && !state.StartsWith("Recycled:", StringComparison.Ordinal) && !state.StartsWith("DeletedPermanently:", StringComparison.Ordinal) && !state.StartsWith("Quarantined:", StringComparison.Ordinal);
    public bool Selected { get => selected; set { if (value && !CanSelect) return; selected = value; Changed(); } }
    public string State { get => state; set { state = value; Changed(); Changed(nameof(CanSelect)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}

public static class Formatting
{
    public static string Duration(double seconds) => $"{(long)(seconds / 3600):00}:{(int)(seconds / 60) % 60:00}:{(int)seconds % 60:00}";
    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes; int i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:0.##} {units[i]}";
    }
}
