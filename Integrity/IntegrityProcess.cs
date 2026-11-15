using System.Diagnostics;
using System.Text;

namespace MediaForge.Integrity;

/// <summary>Drains both pipes, bounds retained output, and kills the process tree on cancel/stall.</summary>
public sealed class IntegrityProcess : IIntegrityRunner
{
    public async Task<ToolRun> RunAsync(string executable, IReadOnlyList<string> arguments, int stallSeconds, Action<string>? outputLine, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("Unable to start FFmpeg tools.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        bool timedOut = false, errors = false, warnings = false, hardwareConfirmed = false;
        long lastProgress = Stopwatch.GetTimestamp();
        string lastTime = "", lastFrame = "";
        var output = new StringBuilder(); var diagnostics = new StringBuilder();
        static void AppendBounded(StringBuilder target, string line, int limit)
        {
            if (target.Length >= limit) return;
            target.AppendLine(line[..Math.Min(line.Length, limit - target.Length)]);
        }
        using var registration = stop.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });
        var stdout = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                AppendBounded(output, line, 2 * 1024 * 1024);
                if (outputLine == null || line.StartsWith("out_time_us=", StringComparison.Ordinal) && line != lastTime || line.StartsWith("frame=", StringComparison.Ordinal) && line != lastFrame)
                    Interlocked.Exchange(ref lastProgress, Stopwatch.GetTimestamp());
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal)) lastTime = line;
                if (line.StartsWith("frame=", StringComparison.Ordinal)) lastFrame = line;
                outputLine?.Invoke(line);
            }
        });
        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                errors |= line.Contains("[error]", StringComparison.OrdinalIgnoreCase) || line.Contains("[fatal]", StringComparison.OrdinalIgnoreCase);
                warnings |= line.Contains("[warning]", StringComparison.OrdinalIgnoreCase);
                hardwareConfirmed |= line.Contains("Format d3d11 chosen by get_format", StringComparison.OrdinalIgnoreCase);
                if (!line.Contains("[debug]", StringComparison.OrdinalIgnoreCase) && !line.Contains("[verbose]", StringComparison.OrdinalIgnoreCase) && !line.Contains("[info]", StringComparison.OrdinalIgnoreCase))
                    AppendBounded(diagnostics, line, 32768);
            }
        });
        var watchdog = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { await Task.Delay(500, stop.Token); } catch (OperationCanceledException) { break; }
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastProgress)).TotalSeconds > stallSeconds)
                { timedOut = true; stop.Cancel(); break; }
            }
        });
        try { await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); }
        finally { stop.Cancel(); await watchdog; }
        cancellation.ThrowIfCancellationRequested();
        return new(process.ExitCode, output.ToString(), diagnostics.ToString(), errors, warnings, timedOut, hardwareConfirmed);
    }
}
