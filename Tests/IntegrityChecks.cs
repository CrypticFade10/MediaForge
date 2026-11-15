using MediaForge.Integrity;
using SimilarVideoFinder.Core;
using System.Security.Cryptography;

namespace SimilarVideoFinder.Tests;

internal static partial class Program
{
    private const string IntegrityProbe = "{\"streams\":[{\"index\":0,\"codec_type\":\"video\",\"codec_name\":\"h264\"},{\"index\":1,\"codec_type\":\"audio\",\"codec_name\":\"aac\"}],\"format\":{\"duration\":\"1\"}}";
    private sealed class ScriptedIntegrityRunner(params ToolRun[] script) : IIntegrityRunner
    {
        private int index;
        public Action<int>? BeforeRun { get; init; }
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<ToolRun> RunAsync(string executable, IReadOnlyList<string> arguments, int stallSeconds, Action<string>? outputLine, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Calls.Add(arguments); BeforeRun?.Invoke(index);
            var result = script[index++];
            if (outputLine != null && result.ExitCode == 0 && !result.TimedOut)
                foreach (var line in new[] { "frame=10", "out_time_us=1000000", "progress=end" }) outputLine(line);
            return Task.FromResult(result);
        }
    }
    private sealed class BlockingIntegrityRunner : IIntegrityRunner
    {
        public async Task<ToolRun> RunAsync(string executable, IReadOnlyList<string> arguments, int stallSeconds, Action<string>? outputLine, CancellationToken cancellation)
        {
            if (outputLine == null) return new(0, IntegrityProbe, "", false, false, false);
            await Task.Delay(Timeout.Infinite, cancellation);
            throw new InvalidOperationException("Unreachable");
        }
    }
    private static async Task IntegrityChecks(string run, string workspace)
    {
        string root = Path.Combine(run, "integrity"); Directory.CreateDirectory(root);
        string nested = Path.Combine(root, "nested"); Directory.CreateDirectory(nested);
        string good = Path.Combine(nested, "日本語 test.mp4");
        string audio = Path.Combine(root, "audio.m4a");
        string bad = Path.Combine(root, "broken.mkv");
        string truncated = Path.Combine(root, "truncated.mp4");
        string ffmpeg = Path.Combine(workspace, "vendor", "ffmpeg.exe"), ffprobe = Path.Combine(workspace, "vendor", "ffprobe.exe");
        await MediaProcessor.RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=10", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "1", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", good], CancellationToken.None);
        await MediaProcessor.RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=44100", "-t", "1", "-c:a", "aac", audio], CancellationToken.None);
        File.WriteAllText(bad, "This is not a media container.");
        var videoBytes = File.ReadAllBytes(good); File.WriteAllBytes(truncated, videoBytes[..(videoBytes.Length / 2)]);
        File.WriteAllText(Path.Combine(root, "ignore.txt"), "Excluded extension");
        var originalHash = SHA256.HashData(videoBytes);
        var scanner = new IntegrityScanner(ffmpeg, ffprobe);
        var options = new IntegrityOptions { Roots = [root, nested], Workers = 2 };
        var report = await scanner.ScanAsync(options, null, null, CancellationToken.None);
        Check(report.Results.Count == 4, "Integrity: recursive discovery deduplicates overlapping roots and filters extensions");
        Check(report.Results.Single(r => r.Path == good).Status == IntegrityStatus.Passed, "Integrity: valid Unicode video fully decodes");
        Check(report.Results.Single(r => r.Path == audio).Status == IntegrityStatus.Passed, "Integrity: audio-only media fully decodes");
        Check(report.Results.Single(r => r.Path == bad).Status == IntegrityStatus.MediaErrors, "Integrity: invalid container reported as media errors");
        Check(report.Results.Single(r => r.Path == truncated).Status == IntegrityStatus.MediaErrors, "Integrity: truncated MP4 reported as media errors");
        string multi = Path.Combine(root, "multiple-audio.mkv");
        await MediaProcessor.RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-y", "-i", good, "-map", "0:v", "-map", "0:a", "-map", "0:a", "-c", "copy", multi], CancellationToken.None);
        var multiResult = await scanner.CheckFileAsync(multi, options, null, CancellationToken.None);
        Check(multiResult.Status == IntegrityStatus.Passed, "Integrity: multiple audio streams decode in one file");
        Check(SHA256.HashData(File.ReadAllBytes(good)).SequenceEqual(originalHash), "Integrity: source media is unchanged");
        var quick = await scanner.CheckFileAsync(good, options with { Mode = CheckMode.QuickInspection }, null, CancellationToken.None);
        Check(quick.Status == IntegrityStatus.Passed && quick.Decoder == "FFprobe" && quick.Summary.Contains("not decoded"), "Integrity: quick inspection explicitly distinguishes inspection from decoding");
        var gpu = await scanner.CheckFileAsync(good, options with { Decoder = DecodeMode.Gpu }, null, CancellationToken.None);
        Check(gpu.Status == IntegrityStatus.Passed && (gpu.Decoder == "GPU (D3D11VA)" || gpu.CpuRetry && gpu.Decoder == "CPU"), "Integrity: real GPU request either confirms hardware or safely retries CPU");
        var gpuAudio = await scanner.CheckFileAsync(audio, options with { Decoder = DecodeMode.Gpu }, null, CancellationToken.None);
        Check(gpuAudio.Status == IntegrityStatus.Passed && gpuAudio.Decoder == "CPU" && !gpuAudio.CpuRetry, "Integrity: audio-only checks skip unnecessary GPU attempts");
        var missing = await scanner.CheckFileAsync(Path.Combine(root, "missing.mp4"), options, null, CancellationToken.None);
        Check(missing.Status == IntegrityStatus.Inaccessible, "Integrity: missing files are inaccessible, not corrupt");
        var folderIssue = await scanner.ScanAsync(options with { Roots = [Path.Combine(root, "unavailable-folder")] }, null, null, CancellationToken.None);
        Check(folderIssue.Results.Single().Status == IntegrityStatus.DiscoveryError, "Integrity: unavailable folders are reported separately");

        var probe = new ToolRun(0, IntegrityProbe, "", false, false, false);
        var pass = new ToolRun(0, "", "", false, false, false);
        var failure = new ToolRun(1, "", "[error] Error while decoding stream #0:0: Invalid data found", true, false, false);
        async Task<IntegrityResult> Script(ToolRun decode, ToolRun? retry = null)
        {
            var runner = new ScriptedIntegrityRunner(retry == null ? [probe, decode] : [probe, decode, retry]);
            return await new IntegrityScanner("ffmpeg", "ffprobe", runner).CheckFileAsync(good, options with { Decoder = DecodeMode.Gpu }, null, CancellationToken.None);
        }
        var fallback = await Script(failure, pass);
        Check(fallback.Status == IntegrityStatus.Passed && fallback.CpuRetry && fallback.Decoder == "CPU", "Integrity: GPU failure alone cannot mark healthy media corrupt");
        var confirmed = await Script(pass with { HardwareConfirmed = true });
        Check(confirmed.Status == IntegrityStatus.Passed && !confirmed.CpuRetry && confirmed.Decoder.Contains("GPU"), "Integrity: confirmed hardware success is labelled GPU");
        var silent = await Script(pass, pass);
        Check(silent.Status == IntegrityStatus.Passed && silent.CpuRetry && silent.Decoder == "CPU", "Integrity: unconfirmed hardware use triggers a CPU retry");
        var bothFail = await Script(failure, failure);
        Check(bothFail.Status == IntegrityStatus.MediaErrors && bothFail.CpuRetry, "Integrity: CPU-confirmed media errors remain failures");
        var hardwareWarning = await Script(pass with { Warnings = true, HardwareConfirmed = true, Diagnostics = "[warning] hardware warning" }, pass);
        Check(hardwareWarning.Status == IntegrityStatus.Passed && hardwareWarning.CpuRetry && hardwareWarning.Diagnostics.Contains("hardware warning"), "Integrity: GPU warnings rechecked on CPU with diagnostics preserved");
        var timeout = await Script(pass with { TimedOut = true }, pass with { TimedOut = true });
        Check(timeout.Status == IntegrityStatus.TimedOut, "Integrity: stalls are inconclusive, not media errors");
        var unsupported = await Script(failure, failure with { Diagnostics = "[error] Unknown decoder xyz" });
        Check(unsupported.Status == IntegrityStatus.Unsupported, "Integrity: unsupported CPU decoder is not labelled corrupt media");
        var ioError = await Script(failure, failure with { Diagnostics = "[error] Input/output error" });
        Check(ioError.Status == IntegrityStatus.Inaccessible, "Integrity: I/O failures are separate from media errors");
        var toolError = await Script(failure, failure with { Diagnostics = "[error] Error initializing output encoder: Cannot allocate memory" });
        Check(toolError.Status == IntegrityStatus.ToolError, "Integrity: runtime resource failures do not label media corrupt");
        var warningRunner = new ScriptedIntegrityRunner(probe, pass with { Warnings = true, Diagnostics = "[warning] Non-monotonic timestamp" });
        var warned = await new IntegrityScanner("ffmpeg", "ffprobe", warningRunner).CheckFileAsync(good, options, null, CancellationToken.None);
        Check(warned.Status == IntegrityStatus.Warnings, "Integrity: successful decoding with warnings requires review");
        var changedRunner = new ScriptedIntegrityRunner(probe, pass) { BeforeRun = index => { if (index == 1) File.SetLastWriteTimeUtc(good, DateTime.UtcNow.AddMinutes(1)); } };
        var changed = await new IntegrityScanner("ffmpeg", "ffprobe", changedRunner).CheckFileAsync(good, options, null, CancellationToken.None);
        Check(changed.Status == IntegrityStatus.Changed, "Integrity: changing files invalidate the verdict");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var stopped = await scanner.ScanAsync(options, null, null, cancelled.Token);
        Check(stopped.Cancelled && stopped.Results.Count == 0, "Integrity: cancelled discovery does not produce false passes");
        using var midScan = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var interrupted = await new IntegrityScanner("ffmpeg", "ffprobe", new BlockingIntegrityRunner()).ScanAsync(options with { Workers = 1 }, null, null, midScan.Token);
        Check(interrupted.Cancelled && interrupted.Results.Any(r => r.Status == IntegrityStatus.Cancelled) && interrupted.Results.All(r => r.Status != IntegrityStatus.Passed), "Integrity: active scan cancellation records interrupted files without false passes");
        var missingTools = await new IntegrityScanner(Path.Combine(root, "no-ffmpeg.exe"), Path.Combine(root, "no-ffprobe.exe")).CheckFileAsync(good, options, null, CancellationToken.None);
        Check(missingTools.Status == IntegrityStatus.ToolError, "Integrity: missing helper programs are tool failures");
        string csv = Path.Combine(root, "report.csv"), json = Path.Combine(root, "report.json");
        IntegrityExport.WriteCsv(csv, report.Results.Append(new("=HYPERLINK(\"bad\")", IntegrityStatus.MediaErrors, CheckMode.FullDecode, "CPU", false, 1, "bad,\"quoted\"\nline", "")));
        IntegrityExport.WriteJson(json, report);
        Check(File.ReadAllText(csv).Contains("'=HYPERLINK") && File.ReadAllText(csv).Contains("bad,\"\"quoted\"\""), "Integrity: CSV escapes quotes, newlines and spreadsheet formulas");
        using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
        Check(saved.RootElement.GetProperty("Results").GetArrayLength() == 4 && saved.RootElement.GetProperty("Options").GetProperty("Mode").GetString() == "FullDecode", "Integrity: JSON preserves check settings and every result");

        // Real helper lifecycle checks, not mock-only cancellation/timeout coverage.
        var processes = new IntegrityProcess();
        var longArguments = new[] { "-v", "level+warning", "-nostdin", "-re", "-f", "lavfi", "-i", "sine=frequency=440", "-t", "30", "-f", "null", "-" };
        var stalled = await processes.RunAsync(ffmpeg, longArguments, 1, _ => { }, CancellationToken.None);
        Check(stalled.TimedOut && stalled.ExitCode != 0, "Integrity: watchdog terminates a helper with no reported progress");
        using var stopProcess = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        bool killed = false;
        try { await processes.RunAsync(ffmpeg, longArguments, 60, _ => { }, stopProcess.Token); } catch (OperationCanceledException) { killed = true; }
        Check(killed, "Integrity: cancellation terminates a running FFmpeg process");
    }
}
