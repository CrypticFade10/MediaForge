using System.Collections.Concurrent;
using System.Diagnostics;

namespace SimilarVideoFinder.Core;

public sealed class ScanEngine(CacheStore cache, MediaProcessor media)
{
    public async Task<ScanReport> ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken cancellation)
    {
        options.Validate();
        var report = new ScanReport { NameThreshold = options.NameThreshold, FrameDistance = options.FrameDistance };
        var errors = new ConcurrentQueue<string>();
        int omittedErrors = 0;
        void Error(string message) { if (errors.Count < 1000) errors.Enqueue(message); else Interlocked.Increment(ref omittedErrors); }
        var clock = Stopwatch.StartNew();
        long lastReport = -1000, bytesRead = 0;
        string lastStage = ""; double stageStarted = 0; int cacheStart = cache.CacheHits;
        object progressGate = new();
        void Update(ScanProgress p)
        {
            lock (progressGate)
            {
                if (p.Stage != lastStage || p.Completed == p.Total || clock.ElapsedMilliseconds - lastReport >= 150)
                {
                    if (lastStage != p.Stage) stageStarted = clock.Elapsed.TotalSeconds;
                    lastStage = p.Stage; lastReport = clock.ElapsedMilliseconds;
                    double elapsed = clock.Elapsed.TotalSeconds;
                    progress?.Report(p with { BytesRead = Interlocked.Read(ref bytesRead), ElapsedSeconds = elapsed, CacheHits = cache.CacheHits - cacheStart,
                        RemainingSeconds = p.Completed > 0 && p.Total > p.Completed ? (elapsed - stageStarted) / p.Completed * (p.Total - p.Completed) : null });
                }
            }
        }
        List<FileRecord> files = [];
        async Task Process(IEnumerable<FileRecord> source, string stage, Func<FileRecord, Task> action)
        {
            var work = source.ToArray(); int completed = 0;
            var changed = new ConcurrentQueue<FileRecord>();
            object saveGate = new(); long lastSave = clock.ElapsedMilliseconds;
            void Flush(bool force = false)
            {
                lock (saveGate)
                {
                    if (!force && changed.Count < 64 && clock.ElapsedMilliseconds - lastSave < 1000) return;
                    var batch = new List<FileRecord>();
                    while (changed.TryDequeue(out var file)) batch.Add(file);
                    if (batch.Count > 0) cache.SaveMany(batch);
                    lastSave = clock.ElapsedMilliseconds;
                }
            }
            Update(new(stage, 0, work.Length));
            try {
            await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = options.Workers, CancellationToken = cancellation }, async (file, token) =>
            {
                var before = (file.SampleHash, file.FullHash, file.Media, file.Frames);
                try
                {
                    if (!file.IsCurrent()) throw new IOException("File changed or is unavailable.");
                    await action(file);
                    if (!file.IsCurrent()) throw new IOException("File changed during processing.");
                    if (before != (file.SampleHash, file.FullHash, file.Media, file.Frames)) changed.Enqueue(file);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or System.Text.Json.JsonException or InvalidOperationException)
                {
                    if (!file.IsCurrent()) { file.SampleHash = null; file.FullHash = null; file.Frames = null; file.Media = null; cache.Remove(file.Path); }
                    else if (before != (file.SampleHash, file.FullHash, file.Media, file.Frames)) changed.Enqueue(file);
                    Error($"{file.Path}: {ex.Message}");
                }
                finally { Flush(); Update(new(stage, Interlocked.Increment(ref completed), work.Length, Detail: file.Path)); }
            });
            } finally { Flush(true); }
        }
        try
        {
            report.ReferenceRoots = options.ReferenceRoots.Select(FileInventory.CanonicalRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            cache.SaveSettings(options); report.ScanId = cache.BeginScan(options);
            FileInventory.Enumerate(options, cache, Update, Error, cancellation, files);
            report.FileCount = files.Count;
            if (options.MinimumDurationSeconds > 0)
            {
                await Process(files.Where(f => f.Media == null), "Checking minimum duration", async f => f.Media = await media.ProbeAsync(f.Path, cancellation));
                files.RemoveAll(f => f.Media == null || f.Media.Duration < options.MinimumDurationSeconds);
            }
            if (options.EpisodeInFolder)
            {
                Update(new("Matching episode numbers within each folder", 0, files.Count));
                report.Groups.AddRange(EpisodeFolderMatcher.Find(files, cancellation));
            }
            if (options.IdenticalFilename)
            {
                Update(new("Matching identical filenames", 0, files.Count));
                foreach (var group in files.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                {
                    cancellation.ThrowIfCancellationRequested();
                    report.Groups.Add(new() { Kind = MatchKind.IdenticalFilename,
                        Members = group.Select(f => new MatchMember(f, new(100, "Same complete filename, ignoring case; contents may differ"))).ToList() });
                }
            }
            if (options.SimilarFilename)
            {
                Update(new("Matching similar filenames", 0, files.Count));
                report.Groups.AddRange(FilenameMatcher.Find(files, options.NameThreshold, cancellation));
            }
            if (options.IdenticalContents)
            {
                var candidates = files.GroupBy(f => f.Size).Where(g => g.Count() > 1).SelectMany(g => g).ToArray();
                int sampleTotal = candidates.Count(f => f.SampleHash == null);
                int sampleCompleted = 0;
                await Process(candidates.Where(f => f.SampleHash == null), "Sampling equal-size files", async f =>
                {
                    if (f.SampleHash == null) f.SampleHash = await HashMatcher.Calculate(f, false,
                        n => { Interlocked.Add(ref bytesRead, n); Update(new("Sampling equal-size files", sampleCompleted, sampleTotal)); }, cancellation);
                    Interlocked.Increment(ref sampleCompleted);
                });
                var fullCandidates = candidates.Where(f => f.SampleHash != null)
                    .GroupBy(f => (f.Size, f.SampleHash)).Where(g => g.Count() > 1).SelectMany(g => g).ToArray();
                int hashCompleted = 0;
                await Process(fullCandidates.Where(f => f.FullHash == null), "Confirming full SHA-256 hashes", async f =>
                {
                    if (f.FullHash == null) f.FullHash = await HashMatcher.Calculate(f, true,
                        n => { Interlocked.Add(ref bytesRead, n); Update(new("Confirming full SHA-256 hashes", hashCompleted, fullCandidates.Length)); }, cancellation);
                    Interlocked.Increment(ref hashCompleted);
                });
                foreach (var group in fullCandidates.Where(f => f.FullHash != null).GroupBy(f => (f.Size, f.FullHash)).Where(g => g.Count() > 1))
                    report.Groups.Add(new() { Kind = MatchKind.IdenticalContents,
                        Members = group.Select(f => new MatchMember(f, new(100, "Equal size and complete SHA-256 hash: " + f.FullHash))).ToList() });
            }
            if (options.VideoContent)
            {
                await Process(files.Where(f => f.Media == null), "Reading video metadata", async f => f.Media = await media.ProbeAsync(f.Path, cancellation));
                // Only duration-compatible files can match; unique-duration videos need no frame reads.
                var sorted = files.Where(f => f.Media != null).OrderBy(f => f.Media!.Duration).ToArray();
                var compatible = sorted.Where((f, i) => i > 0 && VideoMatcher.CompatibleDuration(f.Media!.Duration, sorted[i - 1].Media!.Duration)
                    || i + 1 < sorted.Length && VideoMatcher.CompatibleDuration(f.Media!.Duration, sorted[i + 1].Media!.Duration)).ToArray();
                var aliases = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
                foreach (var set in compatible.Where(f => f.FullHash != null).GroupBy(f => (f.Size, f.FullHash)))
                {
                    var owner = set.FirstOrDefault(f => f.Frames != null) ?? set.First();
                    foreach (var file in set) if (!ReferenceEquals(file, owner)) aliases[file.Path] = owner;
                }
                await Process(compatible.Where(f => f.Frames == null && !aliases.ContainsKey(f.Path)), "Sampling video frames", async f => f.Frames = await media.FingerprintAsync(f, cancellation));
                var reused = new List<FileRecord>();
                foreach (var file in compatible.Where(f => f.Frames == null && aliases.ContainsKey(f.Path)))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var owner = aliases[file.Path];
                    if (owner.Frames != null && owner.IsCurrent() && file.IsCurrent()) { file.Frames = owner.Frames; reused.Add(file); }
                }
                cache.SaveMany(reused);
                foreach (var file in compatible.Where(f => f.Frames != null && f.Frames.Count(p => p.Informative) < 9))
                    Error($"{file.Path}: fewer than nine informative frames; visual matching skipped.");
                Update(new("Comparing video fingerprints", 0, compatible.Length));
                report.Groups.AddRange(VideoMatcher.Find(compatible, options.FrameDistance, cancellation));
            }
        Update(new("Validating results", 0, report.Groups.Count));
        var current = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool Current(FileRecord f)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!current.TryGetValue(f.Path, out var value))
            { current[f.Path] = value = f.IsCurrent(); if (!value) Error($"Result removed because file changed or is unavailable: {f.Path}"); }
            return value;
        }
        ReportMaintenance.Prune(report, Current, cancellation);
        }
        catch (OperationCanceledException) { report.Cancelled = true; report.Status = ScanStatus.Cancelled; }
        catch (Exception ex) { Error("Scan stopped: " + ex.Message); report.Status = ScanStatus.Failed; }
        report.FileCount = files.Count;
        for (int i = 0; i < report.Groups.Count; i++) report.Groups[i].Id = $"{report.ScanId}-{i + 1:D5}";
        report.Errors = errors.ToList();
        if (omittedErrors > 0) report.Errors.Add($"{omittedErrors} additional issues omitted. Scan smaller folder selections to inspect them.");
        if (report.ScanId != 0) cache.FinishScan(report);
        Update(new(report.Status switch { ScanStatus.Cancelled => "Scan cancelled - completed work retained", ScanStatus.Failed => "Scan failed - see Issues", _ => "Scan complete" }, report.FileCount, report.FileCount));
        return report;
    }
}
