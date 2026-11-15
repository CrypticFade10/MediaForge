namespace MediaForge.Integrity;

public enum CheckMode { FullDecode, QuickInspection }
public enum DecodeMode { Cpu, Gpu }
public enum IntegrityStatus { Passed, Warnings, MediaErrors, Inaccessible, Unsupported, TimedOut, Changed, Cancelled, ToolError, DiscoveryError }

public sealed record IntegrityOptions
{
    public List<string> Roots { get; init; } = [];
    public string Extensions { get; init; } = ".mkv;.mp4;.avi;.mov;.m4v;.ts;.m2ts;.mts;.webm;.mpg;.mpeg;.wmv;.flv;.vob;.3gp;.mp3;.flac;.wav;.m4a;.aac;.ogg;.opus;.wma";
    public CheckMode Mode { get; init; } = CheckMode.FullDecode;
    public DecodeMode Decoder { get; init; } = DecodeMode.Cpu;
    public int Workers { get; init; } = 1;
    public int StallTimeoutSeconds { get; init; } = 120;
}

public sealed record IntegrityResult(string Path, IntegrityStatus Status, CheckMode Mode, string Decoder, bool CpuRetry, double ElapsedSeconds, string Summary, string Diagnostics)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string StatusText => Status switch
    {
        IntegrityStatus.MediaErrors => "Media errors", IntegrityStatus.ToolError => "Unable to check",
        IntegrityStatus.DiscoveryError => "Folder issue", IntegrityStatus.TimedOut => "Timed out", _ => Status.ToString()
    };
    public string CheckText => Mode == CheckMode.FullDecode ? "Full decode" : "Quick inspection";
}

public sealed record IntegrityProgress(string Stage, int Discovered, int Completed, string Path = "", double? Percent = null, string Decoder = "");
public sealed record IntegrityReport(DateTime StartedUtc, DateTime FinishedUtc, IntegrityOptions Options, bool Cancelled, IReadOnlyList<IntegrityResult> Results);

public sealed record ToolRun(int ExitCode, string Output, string Diagnostics, bool Errors, bool Warnings, bool TimedOut, bool HardwareConfirmed = false);

public interface IIntegrityRunner
{
    Task<ToolRun> RunAsync(string executable, IReadOnlyList<string> arguments, int stallSeconds, Action<string>? outputLine, CancellationToken cancellation);
}
