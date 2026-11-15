using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MediaForge.Integrity;

public sealed class IntegrityScanner(string ffmpeg, string ffprobe, IIntegrityRunner? runner = null)
{
    private readonly IIntegrityRunner runner = runner ?? new IntegrityProcess();
    private sealed record Snapshot(long Size, long Modified);
    private static Snapshot ReadSnapshot(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new(stream.Length, File.GetLastWriteTimeUtc(path).Ticks);
    }
    public async Task<IntegrityReport> ScanAsync(IntegrityOptions options, IProgress<IntegrityProgress>? progress, Action<IntegrityResult>? completed, CancellationToken cancellation)
    {
        if (options.Workers is < 1 or > 4 || options.StallTimeoutSeconds is < 1 or > 3600) throw new ArgumentException("Choose 1–4 workers and a timeout between 1 and 3600 seconds.");
        if (options.Roots.Count == 0) throw new ArgumentException("Choose at least one folder.");
        var started = DateTime.UtcNow;
        var results = new System.Collections.Concurrent.ConcurrentQueue<IntegrityResult>();
        void Emit(IntegrityResult result) { results.Enqueue(result); completed?.Invoke(result); }
        int done = 0;
        List<string> files = [];
        bool cancelled = false;
        try
        {
            files = Discover(options, (path, count) => progress?.Report(new("Discovering folders", count, 0, path)),
                (path, message) => Emit(new(path, IntegrityStatus.DiscoveryError, options.Mode, "Not checked", false, 0, message, "")), cancellation);
            progress?.Report(new("Checking media", files.Count, 0));
            await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = options.Workers, CancellationToken = cancellation }, async (path, token) =>
            {
                IntegrityResult result;
                try
                {
                    result = await CheckFileAsync(path, options, (percent, decoder) => progress?.Report(new("Checking media", files.Count, Volatile.Read(ref done), path, percent, decoder)), token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                { result = new(path, IntegrityStatus.Cancelled, options.Mode, "Interrupted", false, 0, "Check cancelled before completion.", ""); }
                Emit(result);
                progress?.Report(new("Checking media", files.Count, Interlocked.Increment(ref done), path));
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
        cancelled |= cancellation.IsCancellationRequested;
        progress?.Report(new(cancelled ? "Cancelled; unstarted files were not checked" : "Finished", files.Count, done));
        return new(started, DateTime.UtcNow, options, cancelled, results.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static List<string> Discover(IntegrityOptions options, Action<string, int> progress, Action<string, string> issue, CancellationToken cancellation)
    {
        var extensions = options.Extensions.Split([';', ',', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(e => e.StartsWith('.') ? e : "." + e).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (extensions.Count == 0) throw new ArgumentException("Enter at least one media extension.");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        foreach (var root in options.Roots) stack.Push(Path.GetFullPath(root.Trim()));
        while (stack.TryPop(out var directory))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!directories.Add(directory)) continue;
            progress(directory, files.Count);
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { issue(directory, "Skipped a junction or symbolic link."); continue; }
                if (!Directory.Exists(directory)) { issue(directory, "Folder does not exist or is unavailable."); continue; }
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { issue(entry, "Skipped a junction or symbolic link."); continue; }
                        if ((attributes & FileAttributes.Directory) != 0) stack.Push(entry);
                        else if (extensions.Contains(Path.GetExtension(entry))) files.Add(entry);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { issue(entry, ex.Message); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { issue(directory, ex.Message); }
        }
        return files.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IntegrityResult> CheckFileAsync(string path, IntegrityOptions options, Action<double?, string>? progress, CancellationToken cancellation)
    {
        var watch = Stopwatch.StartNew();
        bool retry = false;
        string decoder = "FFprobe", diagnostics = "";
        IntegrityResult Result(IntegrityStatus status, string summary) => new(path, status, options.Mode, decoder, retry, watch.Elapsed.TotalSeconds, summary, diagnostics);
        Snapshot before;
        try { before = ReadSnapshot(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Result(IntegrityStatus.Inaccessible, ex.Message); }
        try
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Invoke(null, decoder);
            var probe = await runner.RunAsync(ffprobe, ["-v", "level+warning", "-show_entries", "stream=index,codec_type,codec_name:stream_disposition=attached_pic:format=duration", "-of", "json", path], options.StallTimeoutSeconds, null, cancellation);
            diagnostics = probe.Diagnostics;
            if (probe.TimedOut) return Verify(Result(IntegrityStatus.TimedOut, "FFprobe stopped responding; file integrity is unknown."), path, before);
            if (probe.ExitCode != 0) return Verify(Result(Classify(probe), "FFprobe could not inspect this media file."), path, before);
            using var json = JsonDocument.Parse(probe.Output);
            var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            bool hasVideo = streams.Any(s => s.GetProperty("codec_type").GetString() == "video" && !(s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.GetInt32() == 1));
            bool hasAudio = streams.Any(s => s.GetProperty("codec_type").GetString() == "audio");
            if (!hasVideo && !hasAudio) return Verify(Result(IntegrityStatus.Unsupported, "No video or audio streams to check."), path, before);
            if (options.Mode == CheckMode.QuickInspection)
                return Verify(Result(probe.Errors || probe.Warnings ? IntegrityStatus.Warnings : IntegrityStatus.Passed, "Container inspected; media was not decoded."), path, before);
            double duration = 0;
            if (json.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var value)) double.TryParse(value.ToString(), CultureInfo.InvariantCulture, out duration);
            double decodedSeconds = 0;
            long frames = 0;
            bool completedDecode = false;
            async Task<ToolRun> Decode(bool gpu)
            {
                decodedSeconds = 0; frames = 0; completedDecode = false;
                decoder = gpu ? "GPU (D3D11VA)" : "CPU";
                progress?.Invoke(0, decoder);
                var arguments = new List<string> { "-hide_banner", "-nostdin", "-loglevel", gpu ? "repeat+level+debug" : "repeat+level+warning", "-nostats", "-progress", "pipe:1", "-stats_period", "0.5", "-threads", "2", "-hwaccel", gpu ? "d3d11va" : "none" };
                if (gpu) arguments.AddRange(["-hwaccel_output_format", "d3d11"]);
                arguments.AddRange(["-i", path, "-map", "0:V?", "-map", "0:a?", "-sn", "-dn", "-max_error_rate", "0", "-threads", "2", "-f", "null", "-"]);
                return await runner.RunAsync(ffmpeg, arguments, options.StallTimeoutSeconds, line =>
                {
                    if (line.StartsWith("frame=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(6).Trim(), out var count)) frames = count;
                    if (line == "progress=end") completedDecode = true;
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(12), out var time))
                    {
                        decodedSeconds = time / 1_000_000d;
                        progress?.Invoke(duration > 0 ? Math.Clamp(decodedSeconds / duration * 100, 0, 100) : null, decoder);
                    }
                }, cancellation);
            }
            bool gpuAttempt = options.Decoder == DecodeMode.Gpu && hasVideo;
            var decode = await Decode(gpuAttempt);
            string hardwareDiagnostics = "";
            if (gpuAttempt)
            {
                // D3D11VA can silently fall back to software; never claim verified GPU use then.
                bool hardwareFailed = decode.ExitCode != 0 || decode.Errors || decode.Warnings || decode.TimedOut || !completedDecode || !decode.HardwareConfirmed || decodedSeconds <= 0 && frames <= 0;
                if (hardwareFailed)
                {
                    retry = true; hardwareDiagnostics = "GPU attempt (failed, warned, or hardware use not confirmed; not the integrity verdict):\n" + decode.Diagnostics + "\nCPU retry:\n";
                    decode = await Decode(false);
                }
            }
            diagnostics = (probe.Diagnostics + "\n" + hardwareDiagnostics + (gpuAttempt && !retry && !decode.Errors && !decode.Warnings ? "D3D11VA hardware decoding confirmed." : decode.Diagnostics)).Trim();
            if (decode.TimedOut) return Verify(Result(IntegrityStatus.TimedOut, "No decoding progress before the timeout; integrity is unknown."), path, before);
            if (decode.ExitCode != 0 || decode.Errors) return Verify(Result(Classify(decode), "Decoding failed; see diagnostics."), path, before);
            if (!completedDecode) return Verify(Result(IntegrityStatus.ToolError, "FFmpeg did not report a completed decode."), path, before);
            if (decodedSeconds <= 0 && frames <= 0) return Verify(Result(IntegrityStatus.MediaErrors, "No video frames or audio duration could be decoded."), path, before);
            bool shortOutput = duration > 3 && decodedSeconds > 0 && decodedSeconds < duration * .9 && duration - decodedSeconds > 2;
            bool warning = probe.Errors || probe.Warnings || decode.Warnings || shortOutput;
            string summary = shortOutput ? "Decoded duration is shorter than metadata; review for possible truncation." : "All selected video and audio streams decoded to the end.";
            if (retry) summary += " GPU attempt did not pass with confirmed hardware use; this result is from the CPU retry.";
            if (warning && !shortOutput) summary += " Decoder warnings require review.";
            return Verify(Result(warning ? IntegrityStatus.Warnings : IntegrityStatus.Passed, summary), path, before);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        { return Verify(Result(IntegrityStatus.ToolError, "Unable to complete the check: " + ex.Message), path, before); }
    }

    private static IntegrityStatus Classify(ToolRun run)
    {
        string error = run.Diagnostics;
        if (new[] { "Permission denied", "Input/output error", "No such file", "network", "Connection timed out", "Connection reset" }.Any(s => error.Contains(s, StringComparison.OrdinalIgnoreCase))) return IntegrityStatus.Inaccessible;
        if (new[] { "Unknown decoder", "Decoder not found", "not currently supported", "Unsupported codec", "not implemented", "Decoding requested, but no decoder", "encrypted" }.Any(s => error.Contains(s, StringComparison.OrdinalIgnoreCase))) return IntegrityStatus.Unsupported;
        if (new[] { "Invalid data", "Error while decoding", "corrupt", "moov atom not found", "EBML header parsing failed", "Invalid NAL", "partial file", "Invalid frame", "Invalid packet", "Invalid bitstream", "Invalid JPEG", "Invalid PNG", "Error parsing", "error count:" }.Any(s => error.Contains(s, StringComparison.OrdinalIgnoreCase))) return IntegrityStatus.MediaErrors;
        return IntegrityStatus.ToolError;
    }
    private static IntegrityResult Verify(IntegrityResult result, string path, Snapshot before)
    {
        try
        {
            if (ReadSnapshot(path) != before) return result with { Status = IntegrityStatus.Changed, Summary = "File changed during the check. Run it again." };
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return result with { Status = IntegrityStatus.Inaccessible, Summary = "File became unavailable during the check: " + ex.Message }; }
    }
}
