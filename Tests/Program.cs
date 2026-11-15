using SimilarVideoFinder.Core;
using System.Diagnostics;
using System.Text.Json;

namespace SimilarVideoFinder.Tests;

internal static partial class Program
{
    private static int passed;
    private static readonly List<string> checks = [];
    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAIL: " + label);
        passed++; checks.Add(label); Console.WriteLine("PASS: " + label);
    }
    private static FileRecord Record(string path)
    {
        var f = new FileInfo(path); return new() { Path = f.FullName, Size = f.Length, ModifiedTicks = f.LastWriteTimeUtc.Ticks };
    }
    private static MatchEvidence? Names(string a, string b, int threshold = 90) => FilenameMatcher.Compare(FilenameMatcher.Parse(a), FilenameMatcher.Parse(b), threshold);
    private static ScanOptions OnlyNames(string root) => new() { Roots = [root], IdenticalContents = false, SimilarFilename = true, VideoContent = false };
    public static async Task<int> Main(string[] args)
    {
        string workspace = args.Length > 0 ? Path.GetFullPath(args[0]) : Environment.CurrentDirectory;
        string run = Path.Combine(workspace, "artifacts", "tests-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(run);
        var metrics = new Dictionary<string, object>();
        try
        {
            int nas = Array.IndexOf(args, "--nas-root");
            if (nas >= 0) { await NasChecks(args[nas + 1], workspace); return 0; }
            if (args.Contains("--v3")) { await V3Checks(run); Console.WriteLine($"All {passed} V3 checks passed."); return 0; }
            if (args.Contains("--selection")) { BulkSelectionChecks(); Console.WriteLine($"All {passed} bulk-selection checks passed."); return 0; }
            if (args.Contains("--native")) { await DeletionChecks(run); return 0; }
            string a = "Top Gear (2002) - S09E05 - Episode 5 [SDTV][AAC 2.0][h265]-RiVER.mkv";
            string b = "Top Gear (2002) - S09E05 - Episode 5 [SDTV][MP3 2.0][XviD]-tt1628033.avi";
            Check(Names(a, b)?.Score == 100, "Supplied Top Gear names match across codec, audio and extensions");
            (string First, string Second, string Label)[] userPairs =
            [
                ("The Simpsons (1989) - S37e07 - Sashes To Sashes [Hdtv-720P][Aac 2.0][X264]-Ngp.mkv",
                 "The Simpsons (1989) - S37E07 - Sashes to Sashes [WEBDL-1080p][AAC 2.0][h265]-FLUX.mkv", "The Simpsons"),
                ("Dance Moms (2011) - S03E13 - Shes a Maniac [WEBDL-1080p][AAC 2.0][h264].mp4",
                 "Dance Moms (2011) - S03E13 - Shes a Maniac [WEBDL-1080p][AAC 2.0][h265].mkv", "Dance Moms"),
                ("Big Mouth (2017) - S04E05 - A Very Special 9 11 Episode [WEBDL-1080p][AAC 2.0][h264]-MACK4.mkv",
                 "Big Mouth (2017) - S04e05 - A Very Special 9 11 Episode [Webdl-1080P][Aac 2.0][H264]-Mack4.mkv", "Big Mouth"),
                ("SpongeBob SquarePants (1999) - S02E17 - No Free Rides [WEBDL-1080p Proper][EAC3 2.0][h264]-Kitsune.mkv",
                 "SpongeBob SquarePants (1999) - S02E17 - No Free Rides [WEBDL-1080p]{[Mediainfo AudioCodec}[x265]-tt0206512.mkv", "SpongeBob malformed metadata")
            ];
            foreach (var pair in userPairs)
            {
                Check(Names(pair.First, pair.Second)?.Score == 100, "User example: " + pair.Label);
                var pairFiles = new[] { new FileRecord { Path = pair.First }, new FileRecord { Path = pair.Second } };
                Check(FilenameMatcher.Find(pairFiles, 90).Single().Members.Count == 2, "Indexed search groups user example: " + pair.Label);
            }
            Check(Names(a, b.Replace("S09E05", "S09E06")) == null, "Adjacent episodes are rejected");
            var folderExample = new[]
            {
                new FileRecord { Path = @"C:\Folder 1\Show S01E01.mkv" }, new FileRecord { Path = @"C:\Folder 1\Show S01E02.mkv" },
                new FileRecord { Path = @"C:\Folder 1\Show S01E03 first.mkv" }, new FileRecord { Path = @"C:\Folder 1\Completely different title s01e03 second.mp4" },
                new FileRecord { Path = @"C:\Folder 1\Show S01E04.mkv" }, new FileRecord { Path = @"C:\Folder 2\Show S01E01 first.mkv" },
                new FileRecord { Path = @"C:\Folder 2\Other title 1x01 second.mp4" }, new FileRecord { Path = @"C:\Folder 2\Show S01E02.mkv" },
                new FileRecord { Path = @"C:\Folder 2\Show S01E03.mkv" }, new FileRecord { Path = @"C:\Folder 2\Show S01E04.mkv" }
            };
            var folderGroups = EpisodeFolderMatcher.Find(folderExample);
            Check(folderGroups.Count == 2 && folderGroups.All(g => g.Members.Count == 2), "User folder-by-folder example produces exactly two duplicate groups");
            Check(folderGroups.All(g => g.Members.Select(m => Path.GetDirectoryName(m.File.Path)).Distinct().Count() == 1), "Episode search never combines separate folders");
            Check(folderGroups.Any(g => g.Representative.Path.Contains("Folder 1") && g.Members[0].Evidence.Reason.Contains("1:3")) &&
                folderGroups.Any(g => g.Representative.Path.Contains("Folder 2") && g.Members[0].Evidence.Reason.Contains("1:1")), "Folder 1 groups S01E03; Folder 2 groups S01E01");
            Check(EpisodeFolderMatcher.Find([new() { Path = @"C:\Folder 1\Show S01E01.mkv" }, new() { Path = @"C:\Folder 1\Subfolder\Show S01E01.mp4" }]).Count == 0,
                "Parent folder and subfolder have separate episode scopes");
            Check(Names(a, b.Replace("S09E05", "9x05")) != null, "Equivalent 9x05 episode notation");
            Check(Names(a, b.Replace("S09E05", "S08E05")) == null, "Different seasons are rejected");
            Check(Names(a, b.Replace("(2002)", "(2010)")) == null, "Conflicting explicit show years are rejected");
            Check(Names("The Thing (1982) [1080p].mkv", "The Thing (2011) [720p].mp4") == null, "Film remakes are separate");
            Check(Names("The Office S01E01E02.mkv", "The Office 1x01x02.avi") != null, "Multi-episode notation normalises");
            Check(Names("The Office S01E01-E03.mkv", "The Office S01E01E02E03.mp4") != null, "Episode ranges expand correctly");
            Check(Names("The Office S01E01E02.mkv", "The Office S01E01.mp4") == null, "Single and multi-episode files remain separate");
            Check(Names("Movie [Directors Cut].mkv", "Movie.mkv") == null, "Meaningful edition tags are preserved");
            Check(Names("One Two Three Four Five.mkv", "One Two Three Four Six.mkv", 90) == null &&
                Names("One Two Three Four Five.mkv", "One Two Three Four Six.mkv", 80) != null, "Fuzzy threshold changes candidate results");
            var chain = new[] { "one two three four five six seven eight nine.mkv", "one two three four five six seven eight ten.mkv", "one two three four five six seven ten eleven.mkv" }
                .Select(n => new FileRecord { Path = n }).ToArray();
            var nameGroups = FilenameMatcher.Find(chain, 85);
            Check(nameGroups.Count == 2 && nameGroups.All(g => g.Members.Count == 2), "Filename indexing preserves both direct edges in a non-transitive chain");
            var random = new Random(42);
            for (int trial = 0; trial < 10; trial++)
            {
                var generated = Enumerable.Range(0, 120).Select(_ => new FileRecord { Path = string.Join(' ', Enumerable.Range(0, 5).Select(_ => "token" + random.Next(25)).Distinct()) + ".mkv" }).ToArray();
                var indexed = FilenameMatcher.Find(generated, 70);
                Check(indexed.All(g => g.Members.Skip(1).All(m => Names(g.Representative.Name, m.File.Name, 70) != null)), "Indexed filename groups retain direct evidence " + trial);
            }

            string inventory = Path.Combine(run, "inventory"); string sub = Path.Combine(inventory, "nested"); Directory.CreateDirectory(sub);
            string f1 = Path.Combine(inventory, "same.mkv"), f2 = Path.Combine(sub, "SAME.mkv"), f3 = Path.Combine(sub, "unrelated.avi");
            byte[] contents = new byte[200000]; random.NextBytes(contents);
            File.WriteAllBytes(f1, contents); File.WriteAllBytes(f2, contents); File.WriteAllBytes(f3, contents);
            File.WriteAllText(Path.Combine(sub, "not-video.txt"), "Ignored");
            string diff = Path.Combine(sub, "different.mkv"); var other = (byte[])contents.Clone(); other[100000] ^= 255; File.WriteAllBytes(diff, other);
            var media = new MediaProcessor(Path.Combine(workspace, "vendor", "ffmpeg.exe"), Path.Combine(workspace, "vendor", "ffprobe.exe"));
            using var cache = new CacheStore(Path.Combine(run, "cache.sqlite"), true);
            var overlap = OnlyNames(inventory); overlap.Roots.Add(sub);
            var files = FileInventory.Enumerate(overlap, cache, _ => { }, _ => { }, CancellationToken.None);
            Check(files.Count == 4, "Overlapping roots are deduplicated and non-video extensions ignored");
            var shallow = OnlyNames(inventory); shallow.Recursive = false;
            Check(FileInventory.Enumerate(shallow, cache, _ => { }, _ => { }, CancellationToken.None).Count == 1, "Non-recursive selection");
            string unicode = Path.Combine(sub, "日本語 Ω.mkv"); File.WriteAllBytes(unicode, contents);
            Check(FileInventory.Enumerate(OnlyNames(inventory), cache, _ => { }, _ => { }, CancellationToken.None).Count == 5, "Unicode paths");
            string longDirectory = sub;
            for (int i = 0; i < 8; i++) longDirectory = Path.Combine(longDirectory, new string((char)('a' + i), 35));
            Directory.CreateDirectory(longDirectory); File.WriteAllBytes(Path.Combine(longDirectory, "long-video.mkv"), contents);
            Check(FileInventory.Enumerate(OnlyNames(inventory), cache, _ => { }, _ => { }, CancellationToken.None).Count == 6, "Paths longer than 260 characters");
            string junction = Path.Combine(inventory, "loop");
            var junctionProcess = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", junction, inventory }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (junctionProcess != null) { await junctionProcess.WaitForExitAsync(); if (junctionProcess.ExitCode == 0) { Check(true, "Disposable junction fixture created"); Check(FileInventory.Enumerate(OnlyNames(inventory), cache, _ => { }, _ => { }, CancellationToken.None).Count == 6, "Directory junction cycles are skipped"); } else Console.WriteLine("SKIP: junction fixture unavailable in this environment; unrelated tests continue."); }

            var engine = new ScanEngine(cache, media);
            var options = new ScanOptions { Roots = [inventory], VideoContent = false };
            var scan = await engine.ScanAsync(options, null, CancellationToken.None);
            Check(scan.Groups.Count(g => g.Kind == MatchKind.IdenticalFilename) == 1, "Case-insensitive identical complete filenames");
            var hashes = scan.Groups.Single(g => g.Kind == MatchKind.IdenticalContents);
            Check(hashes.Members.Count == 5 && hashes.Members.All(m => m.File.Path != diff), "Full hashes find renamed copies and reject changed equal-size files");
            long cachedBytes = -1;
            await engine.ScanAsync(options, new DirectProgress(p => cachedBytes = p.BytesRead), CancellationToken.None);
            Check(cachedBytes == 0, "Repeated scans reuse complete cached hashes");
            options.ForceRescan = true; long forcedBytes = 0;
            await engine.ScanAsync(options, new DirectProgress(p => forcedBytes = p.BytesRead), CancellationToken.None);
            Check(forcedBytes >= 5 * contents.Length, "Forced rescan reads data again");
            options.ForceRescan = false;
            var snapshot = Record(f1); Check(cache.Load(f1, snapshot.Size, snapshot.ModifiedTicks)?.FullHash != null, "Completed hash persisted");
            File.SetLastWriteTimeUtc(f1, DateTime.UtcNow.AddMinutes(2));
            Check(!snapshot.IsCurrent() && cache.Load(f1, snapshot.Size, File.GetLastWriteTimeUtc(f1).Ticks) == null, "Modification invalidates cache and snapshots");
            var inaccessible = OnlyNames(Path.Combine(run, "missing"));
            var missing = await engine.ScanAsync(inaccessible, null, CancellationToken.None);
            Check(missing.Errors.Count > 0 && missing.FileCount == 0, "Unavailable folder reports an issue without crashing");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Check((await engine.ScanAsync(options, null, cancelled.Token)).Cancelled, "Cancellation saves scan state");
            Check(cache.History().Count >= 5 && cache.LatestReport() != null, "Scan history and reports persist");
            ReportExporter.WriteCsv(Path.Combine(run, "report.csv"), scan);
            Check(File.ReadAllText(Path.Combine(run, "report.csv")).Contains("SHA-256"), "CSV export includes match evidence");

            string videoDir = Path.Combine(run, "videos"); Directory.CreateDirectory(videoDir);
            string original = Path.Combine(videoDir, a); string transcoded = Path.Combine(videoDir, "unrelated-transcode.mp4");
            string resized = Path.Combine(videoDir, b); string distinct = Path.Combine(videoDir, "different-content.mp4");
            await MakeVideo(media, ["-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24", "-t", "8", "-c:v", "libx264", "-crf", "18", original]);
            await MakeVideo(media, ["-i", original, "-vf", "scale=160:90", "-c:v", "libx265", "-x265-params", "pools=1:frame-threads=1:log-level=error", "-crf", "26", transcoded]);
            await MakeVideo(media, ["-i", original, "-vf", "scale=240:134", "-c:v", "mpeg4", "-q:v", "5", resized]);
            string letterboxed = Path.Combine(videoDir, "letterboxed-transcode.mp4");
            await MakeVideo(media, ["-i", original, "-vf", "pad=400:240:40:30:black", "-c:v", "libx264", "-crf", "24", letterboxed]);
            await MakeVideo(media, ["-f", "lavfi", "-i", "mandelbrot=size=320x180:rate=24", "-t", "8", "-c:v", "libx264", distinct]);
            string intro = Path.Combine(videoDir, "shared-intro.mp4");
            await MakeVideo(media, ["-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24:duration=1", "-f", "lavfi", "-i", "smptebars=size=320x180:rate=24:duration=7",
                "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v]", "-map", "[v]", "-c:v", "libx264", intro]);
            using var videoCache = new CacheStore(Path.Combine(run, "video-cache.sqlite"), true);
            var videoEngine = new ScanEngine(videoCache, media);
            var videoOptions = new ScanOptions { Roots = [videoDir], IdenticalFilename = false, IdenticalContents = false, SimilarFilename = false, VideoContent = true };
            using var midCancel = new CancellationTokenSource();
            var interrupted = await videoEngine.ScanAsync(videoOptions, new DirectProgress(p => { if (p.Stage == "Sampling video frames" && p.Completed >= 1) midCancel.Cancel(); }), midCancel.Token);
            Check(interrupted.Cancelled && Directory.GetFiles(videoDir).Any(p => { var f = Record(p); return videoCache.Load(p, f.Size, f.ModifiedTicks)?.Frames?.Count == 12; }), "Interrupted visual scan persists completed fingerprints");
            var watch = Stopwatch.StartNew();
            var videoScan = await videoEngine.ScanAsync(videoOptions, null, CancellationToken.None); watch.Stop(); metrics["VisualScanSeconds"] = watch.Elapsed.TotalSeconds;
            var visual = videoScan.Groups.Where(g => g.Kind == MatchKind.VideoContent).ToArray();
            File.WriteAllText(Path.Combine(run, "visual-report.json"), JsonSerializer.Serialize(videoScan, new JsonSerializerOptions { WriteIndented = true }));
            Check(visual.Length == 1 && visual[0].Members.Count == 4, "Visual matching finds H.265, MPEG-4 and resized re-encodes with unrelated filenames");
            Check(visual[0].Members.Any(m => m.File.Path == letterboxed), "Letterbox and pillarbox borders normalise before visual matching");
            Check(visual[0].Members.All(m => m.File.Path != distinct && m.File.Path != intro), "Unrelated video and shared-intro video are rejected");
            Check(visual[0].Members.Skip(1).All(m => m.Evidence.FrameDistances?.Count == 12), "Per-frame evidence retained");
            var changedDuration = new FileRecord { Path = "longer", Media = new(30, 320, 180, "", ""), Frames = visual[0].Representative.Frames };
            Check(VideoMatcher.Compare(visual[0].Representative, changedDuration, 10) == null, "Duration differences reject visual candidates");
            Check(!PerceptualHash.Compute(new byte[160 * 90], 160, 90, 0).Informative, "Black/constant frames never establish duplicate evidence");
            using (var reopened = new CacheStore(Path.Combine(run, "video-cache.sqlite"), true))
                Check(reopened.Load(visual[0].Representative.Path, visual[0].Representative.Size, visual[0].Representative.ModifiedTicks)?.Frames?.Count == 12,
                    "Restart reloads fingerprints from SQLite");
            string corrupt = Path.Combine(videoDir, "broken.mkv"); File.WriteAllText(corrupt, "not a video");
            Check((await videoEngine.ScanAsync(videoOptions, null, CancellationToken.None)).Errors.Any(e => e.Contains("broken.mkv")), "Corrupt videos report errors and remaining videos still match");
            bool timeout = false;
            try { await MediaProcessor.RunAsync(media.FFmpegPath, ["-v", "error", "-re", "-f", "lavfi", "-i", "testsrc", "-t", "20", "-f", "null", "-"], CancellationToken.None, 1); }
            catch (TimeoutException) { timeout = true; }
            Check(timeout, "Decoder timeout kills the subprocess and returns an issue");

            BulkSelectionChecks();
            await DeletionChecks(run);
            await ImprovementChecks(run, media, original, metrics);
            GC.Collect(); long before = Process.GetCurrentProcess().PrivateMemorySize64;
            var large = Enumerable.Range(0, 100000).Select(i => new FileRecord { Path = $@"C:\library\Movie {i:D6}.mkv", Size = (long)i * 1000000 }).ToArray();
            watch.Restart(); var largeGroups = FilenameMatcher.Find(large, 90); watch.Stop();
            long memory = Process.GetCurrentProcess().PrivateMemorySize64 - before;
            metrics["Filename100000Seconds"] = watch.Elapsed.TotalSeconds; metrics["Filename100000MemoryDeltaMB"] = memory / 1048576d;
            Check(largeGroups.Count == 0 && watch.Elapsed < TimeSpan.FromSeconds(30), "100,000 filename records avoid exhaustive pair comparisons");
            Check(memory < 700L * 1024 * 1024, "100,000 record filename matching stays below 700 MB additional private memory");
            Console.WriteLine($"\nAll {passed} checks passed. Fixtures: {videoDir}");
            File.WriteAllText(Path.Combine(run, "validation.json"), JsonSerializer.Serialize(new { Passed = passed, Checks = checks, Metrics = metrics, VideoFixtures = videoDir }, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(workspace, "artifacts", "latest-fixtures.txt"), videoDir);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex); File.WriteAllText(Path.Combine(run, "failure.txt"), ex.ToString()); return 1;
        }
    }
    private static Task<byte[]> MakeVideo(MediaProcessor media, string[] arguments) => MediaProcessor.RunAsync(media.FFmpegPath,
        new[] { "-y", "-v", "error", "-threads", "1", "-filter_threads", "1", "-filter_complex_threads", "1" }.Concat(arguments), CancellationToken.None, 120);
    private static async Task ImprovementChecks(string run, MediaProcessor media, string original, Dictionary<string, object> metrics)
    {
        Check(Names("Movie [1080p Directors Cut].mkv", "Movie.mkv", 50) == null, "Mixed technical brackets retain Director's Cut metadata even at low thresholds");
        Check(Names("Movie [1080p Extended].mkv", "Movie [720p Theatrical].mkv", 50) == null, "Explicit film editions cannot be grouped together");
        Check(Names("Movie [1080p Directors Cut].mkv", "Movie [720p Directors Cut].mp4") != null, "Equivalent editions still match across technical tags");
        var names = new[] { "Show S01E01.mkv", "Show (2000) S01E01.mkv", "Show (2010) S01E01.mkv" }.Select(p => new FileRecord { Path = p }).ToArray();
        foreach (var order in new[] { names, names.Reverse().ToArray(), new[] { names[1], names[0], names[2] } })
            Check(FilenameMatcher.Find(order, 90).All(g => g.Members.Select(m => FilenameMatcher.Parse(m.File.Name).Year).Where(y => y != null).Distinct().Count() <= 1), "Filename groups reject conflicting explicit years regardless of a yearless reference");

        string privatePath = Path.Combine(run, "private-history", "library.sqlite");
        using (var session = new CacheStore(privatePath))
        {
            session.SaveSettings(new() { Roots = ["private-folder"] });
            long id = session.BeginScan(new() { Roots = ["private-folder"] });
            session.Save(new() { Path = "private-video.mkv", FullHash = "private-hash" });
            session.FinishScan(new() { ScanId = id, Errors = ["private-error"] });
            Check(!session.IsPersistent && !Directory.Exists(Path.GetDirectoryName(privatePath)), "History defaults off and creates no database, path cache or settings directory");
        }
        using (var session = new CacheStore(privatePath)) Check(session.LoadSettings() == null && session.History().Count == 0, "Session history and folders are forgotten after reopening");
        using (var saved = new CacheStore(privatePath, true))
        {
            saved.SaveSettings(new() { Roots = ["remembered-folder"] });
            long id = saved.BeginScan(new() { Roots = ["remembered-folder"] });
            saved.FinishScan(new() { ScanId = id, Status = ScanStatus.Failed, ReferenceRoots = [@"C:\reference"] });
        }
        using (var saved = new CacheStore(privatePath, true)) Check(saved.LoadSettings()?.Roots.Single() == "remembered-folder" && saved.LatestReport()?.Status == ScanStatus.Failed && saved.LatestReport()?.ReferenceRoots.Count == 1, "Opted-in folders, scan status and reference policy survive restart");
        using (var session = new CacheStore(privatePath)) Check(session.LoadSettings() == null && session.History().Count == 0, "Turning history off ignores previously saved paths and reports");

        var a = new FileRecord { Path = @"C:\copies\a.mkv", Size = 100_000_000 };
        var b = new FileRecord { Path = @"C:\copies\b.mkv", Size = 20_000_000 };
        var c = new FileRecord { Path = @"C:\copies\c.mkv", Size = 10_000_000 };
        MatchGroup Exact(params FileRecord[] records) => new() { Id = "exact", Kind = MatchKind.IdenticalContents, Members = records.Select(f => new MatchMember(f, new(100, "SHA-256"))).ToList() };
        var report = new ScanReport { Groups = [Exact(a, b, c)] };
        ReportMaintenance.Prune(report, f => f != a);
        Check(report.Groups.Single().Members.Count == 2 && report.Groups[0].Representative == b, "Deleting an exact-match reference preserves the remaining duplicates");
        var row = new ResultRow { Group = report.Groups[0], Member = report.Groups[0].Members[0] };
        Check(row.SizeBytes == b.Size && row.ScoreValue == 100 && row.Score == "Confirmed", "Result rows expose numeric sorting separately from confirmed-match labels");
        Check(Formatting.Duration(27 * 3600 + 61) == "27:01:01", "Durations longer than 24 hours do not wrap");
        Check(ReferencePolicy.IsWithin(@"C:\REF\nested\a.mkv", @"c:\ref") && !ReferencePolicy.IsWithin(@"C:\reference-other\a.mkv", @"C:\reference"), "Reference path boundaries include nested folders and exclude sibling prefixes");
        a.IsReference = true;
        report = new ScanReport { ReferenceRoots = [@"C:\copies\reference"], Groups = [Exact(a, b, c)] };
        var selected = BulkSelection.Create(report, new HashSet<string>([b.Path, c.Path], StringComparer.OrdinalIgnoreCase), KeepCopyPolicy.LargestFile);
        Check(selected.SelectedPaths.SetEquals([b.Path, c.Path]) && !selected.SelectedPaths.Contains(a.Path) && selected.CopiesKept == 1, "Bulk selection keeps and counts a protected reference even outside the visible filter");
        Check(DeletionService.ValidateSelection(report, new HashSet<string>([a.Path])) != null, "Deletion rejects protected reference files independently of the GUI");
        var noReference = new ScanReport { ReferenceRoots = [@"C:\reference"], Groups = [Exact(b, c)] };
        Check(BulkSelection.Create(noReference, new HashSet<string>([b.Path, c.Path]), KeepCopyPolicy.LargestFile).SelectedPaths.Count == 0, "Reference mode skips groups without a reference match");
        var titleNames = new[] { new FileRecord { Path = @"C:\outside\Show S01E01.mkv" }, new FileRecord { Path = @"C:\reference\Show (2000) S01E01.mkv", IsReference = true } };
        var fuzzy = new ScanReport { ReferenceRoots = [@"C:\reference"], Groups = FilenameMatcher.Find(titleNames, 90) };
        Check(ReferencePolicy.EligiblePaths(fuzzy).Contains(titleNames[0].Path), "A protected non-representative still enables its direct filename matches");

        string deletionDir = Path.Combine(run, "new-deletion-tests"); Directory.CreateDirectory(deletionDir);
        FileRecord Create(string name) { string p = Path.Combine(deletionDir, name); File.WriteAllText(p, "disposable fixture"); return Record(p); }
        var keeper = Create("keeper.mkv"); var first = Create("audit-first.mkv"); var second = Create("audit-second.mkv");
        var audited = new ScanReport { Groups = [Exact(keeper, first, second)] };
        var backend = new FakeBackend(); var service = new DeletionService(backend);
        var outcomes = service.Execute(audited, new HashSet<string>([first.Path, second.Path]), true, auditWriter: _ => throw new IOException("Simulated audit disk full"));
        Check(outcomes.Count == 2 && outcomes.All(o => o.State == DeleteState.DeletedPermanently && o.AuditWarning != null), "Late audit failures preserve actual completed deletion outcomes");
        first = Create("preflight.mkv"); var blocked = Path.Combine(deletionDir, "blocked"); File.WriteAllText(blocked, "not a directory");
        bool preflightFailed = false;
        try { service.Execute(new() { Groups = [Exact(keeper, first)] }, new HashSet<string>([first.Path]), true, Path.Combine(blocked, "audit.jsonl")); } catch (IOException) { preflightFailed = true; }
        Check(preflightFailed && File.Exists(first.Path), "An unwritable audit aborts before any file operation");
        Check(DeletionPolicy.IsNetworkPath(@"\\server\share\movie.mkv") && DeletionPolicy.IsNetworkPath(@"\\?\UNC\server\share\movie.mkv") && !DeletionPolicy.IsNetworkPath(@"\\?\C:\movie.mkv"), "Deletion policy identifies UNC shares without treating extended local paths as network files");
        var networkCopy = Create("network-direct.mkv");
        var networkBackend = new FakeBackend(); var direct = new DeletionService(networkBackend, _ => true);
        var directReport = new ScanReport { Groups = [Exact(keeper, networkCopy)] };
        Check(direct.Execute(directReport, new HashSet<string>([networkCopy.Path]), false).Single().State == DeleteState.RequiresPermanentConfirmation && networkBackend.RecycleCalls == 0 && networkBackend.PermanentCalls == 0 && File.Exists(networkCopy.Path), "Network files never attempt recycling and remain intact until permanent confirmation");
        Check(direct.Execute(directReport, new HashSet<string>([networkCopy.Path]), false, networkPermanentConfirmed: true).Single().State == DeleteState.DeletedPermanently && networkBackend.RecycleCalls == 0 && networkBackend.PermanentCalls == 1 && !File.Exists(networkCopy.Path), "Confirmed network files are deleted directly without a Recycle Bin attempt");
        networkCopy = Create("network-mixed.mkv"); var localCopy = Create("local-mixed.mkv");
        var mixedBackend = new FakeBackend(); var mixedService = new DeletionService(mixedBackend, p => p == networkCopy.Path);
        var mixedReport = new ScanReport { Groups = [Exact(keeper, networkCopy, localCopy)] };
        var mixed = mixedService.Execute(mixedReport, new HashSet<string>([networkCopy.Path, localCopy.Path]), false, networkPermanentConfirmed: true);
        Check(mixed.Single(o => o.Path == networkCopy.Path).State == DeleteState.DeletedPermanently && mixed.Single(o => o.Path == localCopy.Path).State == DeleteState.RequiresPermanentConfirmation && mixedBackend.RecycleCalls == 1 && File.Exists(localCopy.Path), "Network confirmation does not authorize permanent fallback for local files in mixed selections");
        var protectedCopy = Create("protected.mkv"); var candidate = Create("reference-candidate.mkv");
        protectedCopy.IsReference = true;
        var referenceReport = new ScanReport { Groups = [Exact(protectedCopy, keeper, candidate)] };
        File.SetLastWriteTimeUtc(protectedCopy.Path, DateTime.UtcNow.AddMinutes(5));
        Check(service.Execute(referenceReport, new HashSet<string>([candidate.Path]), true).Single().State == DeleteState.Skipped && File.Exists(candidate.Path), "Unavailable or changed protected references block deletion even with other retained copies");
        Check(new ShellDeletionBackend().DeletePermanently(protectedCopy).State == DeleteState.Skipped && File.Exists(protectedCopy.Path), "Permanent backend independently refuses marked reference files");
        string referenceRoot = Path.Combine(run, "inventory-reference"); string candidateRoot = Path.Combine(run, "inventory-candidates");
        Directory.CreateDirectory(referenceRoot); Directory.CreateDirectory(candidateRoot);
        File.WriteAllText(Path.Combine(referenceRoot, "keep.mkv"), "disposable identical reference content");
        File.WriteAllText(Path.Combine(candidateRoot, "other.mkv"), "disposable identical reference content");
        using (var session = new CacheStore("unused"))
        {
            var referenceScan = await new ScanEngine(session, media).ScanAsync(new() { Roots = [candidateRoot], ReferenceRoots = [referenceRoot], SimilarFilename = false, IdenticalFilename = false, EpisodeInFolder = false }, null, CancellationToken.None);
            var exact = referenceScan.Groups.Single();
            Check(referenceScan.FileCount == 2 && exact.Members.Count(m => m.File.IsReference) == 1 && ReferencePolicy.EligiblePaths(referenceScan).SetEquals([Path.Combine(candidateRoot, "other.mkv")]), "Scanning includes reference folders and restricts deletion to their outside SHA-256 matches");
            var referenceOnly = await new ScanEngine(session, media).ScanAsync(new() { ReferenceRoots = [referenceRoot], IdenticalContents = false }, null, CancellationToken.None);
            Check(referenceOnly.FileCount == 1 && referenceOnly.Status == ScanStatus.Completed, "Reference-only folder selections scan successfully without enabling deletion");
        }
        var fuzzyFiles = new[] { "one two three four five six seven eight nine.mkv", "one two three four five six seven eight ten.mkv", "one two three four five six seven ten eleven.mkv" }.Select(p => new FileRecord { Path = p }).ToArray();
        var fuzzySurvivors = new ScanReport { NameThreshold = 85, Groups = FilenameMatcher.Find(new[] { fuzzyFiles[1], fuzzyFiles[0], fuzzyFiles[2] }, 85) };
        ReportMaintenance.Prune(fuzzySurvivors, f => f != fuzzyFiles[1]);
        Check(fuzzySurvivors.Groups.Count == 0, "Removing a fuzzy representative never invents direct evidence between incompatible survivors");
        var cancelledPrune = new ScanReport { Groups = [Exact(a, b)] };
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); bool observed = false;
            try { ReportMaintenance.Prune(cancelledPrune, _ => throw new Exception("Should not read files"), cancel.Token); } catch (OperationCanceledException) { observed = true; }
            Check(observed && cancelledPrune.Groups.Count == 0, "Result validation checks cancellation before reading files and discards unvalidated groups");
        }

        using (var session = new CacheStore("unused"))
        {
            var failed = await new ScanEngine(session, media).ScanAsync(new() { Roots = ["\0invalid"] }, null, CancellationToken.None);
            Check(failed.Status == ScanStatus.Failed && !failed.Cancelled && failed.Errors.Count > 0, "Unexpected scan failures are distinct from user cancellation");
        }
        string inventoryDir = Path.Combine(run, "partial-inventory"); Directory.CreateDirectory(inventoryDir);
        for (int i = 0; i < 260; i++) File.WriteAllText(Path.Combine(inventoryDir, $"file{i}.mkv"), "fixture");
        using (var session = new CacheStore("unused"))
        using (var cancel = new CancellationTokenSource())
        {
            var partial = await new ScanEngine(session, media).ScanAsync(OnlyNames(inventoryDir), new DirectProgress(p => { if (p.Stage == "Discovering videos") cancel.Cancel(); }), cancel.Token);
            Check(partial.Status == ScanStatus.Cancelled && partial.FileCount >= 256, "Cancellation preserves partial inventory statistics");
        }
        var video = Record(original); video.Media = await media.ProbeAsync(original, CancellationToken.None);
        var timer = Stopwatch.StartNew(); var batch = await media.FingerprintAsync(video, CancellationToken.None); timer.Stop(); metrics["BatchedFingerprintSeconds"] = timer.Elapsed.TotalSeconds;
        timer.Restart(); var sequential = new List<FrameFingerprint>();
        for (int i = 0; i < 12; i++) sequential.Add(PerceptualHash.Compute(await media.FrameAsync(original, video.Media.Duration * MediaProcessor.Position(i), CancellationToken.None), 160, 90, i));
        timer.Stop(); metrics["IndividualFrameSeconds"] = timer.Elapsed.TotalSeconds;
        Check(batch.SequenceEqual(sequential), "Batched extraction preserves all twelve original accurate-seek fingerprints");
        var preview = await media.PreviewAsync(original, 1, CancellationToken.None);
        Check(preview.Length > 8 && preview.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Comparison previews use colour PNG images rather than tiny grayscale buffers");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); bool observed = false;
            try { await media.PreviewAsync(original, 1, cancelled.Token); } catch (OperationCanceledException) { observed = true; }
            Check(observed, "Preview decoding observes cancellation");
        }
        string exactDir = Path.Combine(run, "fingerprint-reuse"); Directory.CreateDirectory(exactDir);
        File.Copy(original, Path.Combine(exactDir, "one.mkv")); File.Copy(original, Path.Combine(exactDir, "two.mkv"));
        using (var session = new CacheStore("unused"))
        {
            int before = media.FingerprintRuns;
            var reused = await new ScanEngine(session, media).ScanAsync(new() { Roots = [exactDir], VideoContent = true }, null, CancellationToken.None);
            Check(reused.Groups.Any(g => g.Kind == MatchKind.VideoContent && g.Members.Count == 2) && media.FingerprintRuns - before == 1, "Full SHA-256 duplicate files reuse a single fingerprint extraction");
            before = media.FingerprintRuns;
            await new ScanEngine(session, media).ScanAsync(new() { Roots = [exactDir], VideoContent = true }, null, CancellationToken.None);
            Check(media.FingerprintRuns == before, "Repeated session scans reuse completed fingerprints without extracting frames");
        }
        string subtitles = Path.Combine(run, "tracks.srt"); File.WriteAllText(subtitles, "1\n00:00:00,000 --> 00:00:01,000\nFixture subtitles\n");
        string tracks = Path.Combine(run, "multiple-tracks.mkv");
        await MakeVideo(media, ["-i", original, "-f", "lavfi", "-i", "sine=frequency=440:duration=8", "-f", "lavfi", "-i", "sine=frequency=880:duration=8", "-i", subtitles,
            "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0", "-map", "3:s:0", "-c:v", "copy", "-c:a", "aac", "-c:s", "srt", "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=fra", "-metadata:s:s:0", "language=spa", tracks]);
        var info = await media.ProbeAsync(tracks, CancellationToken.None);
        Check(info.AudioTracks.Count == 2 && info.SubtitleTracks.Count == 1 && info.AudioTracks[1].Language == "fra" && info.SubtitleTracks[0].Language == "spa" && info.BitDepth > 0, "Metadata retains all audio/subtitle tracks, languages and bit depth");
    }
    private static void BulkSelectionChecks()
    {
        FileRecord File(string name, long size) => new() { Path = $@"C:\bulk\{name}.mkv", Size = size };
        MatchGroup Group(string id, params FileRecord[] files) => new() { Id = id, Kind = MatchKind.IdenticalContents,
            Members = files.Select(f => new MatchMember(f, new(100, "fixture"))).ToList() };
        HashSet<string> Visible(params FileRecord[] files) => files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var a = File("a", 300); var b = File("b", 100); var c = File("c", 200);
        var report = new ScanReport { Groups = [Group("one", c, a, b)] };
        var largest = BulkSelection.Create(report, Visible(a, b, c), KeepCopyPolicy.LargestFile);
        Check(largest.SelectedPaths.SetEquals(Visible(b, c)) && largest.Groups == 1 && largest.CopiesKept == 1,
            "Bulk selection keeps the largest copy and selects every other member");
        Check(BulkSelection.Create(report, Visible(a, b, c), KeepCopyPolicy.SmallestFile).SelectedPaths.SetEquals(Visible(a, c)),
            "Bulk selection can keep the smallest copy");
        Check(BulkSelection.Create(report, Visible(a, b, c), KeepCopyPolicy.FirstResult).SelectedPaths.SetEquals(Visible(a, b)),
            "Bulk selection can keep the first scan result");
        var filtered = BulkSelection.Create(report, Visible(a, b), KeepCopyPolicy.LargestFile);
        Check(filtered.SelectedPaths.SetEquals(Visible(b)) && !filtered.SelectedPaths.Contains(c.Path), "Bulk selection never newly selects filtered-out paths");
        Check(BulkSelection.Create(report, Visible(a), KeepCopyPolicy.LargestFile).SelectedPaths.Count == 0,
            "Groups with only one visible eligible copy are skipped");
        Check(BulkSelection.Create(report, Visible(), KeepCopyPolicy.LargestFile).SelectedPaths.Count == 0, "Empty filtered results select nothing");
        var equal = File("aa", 300);
        var tied = new ScanReport { Groups = [Group("ties", equal, a)] };
        Check(!BulkSelection.Create(tied, Visible(a, equal), KeepCopyPolicy.LargestFile).SelectedPaths.Contains(a.Path),
            "Equal sizes use a deterministic path tie-break");
        var overlap = new ScanReport { Groups = [Group("ab", a, b), Group("bc", b, c), Group("ac", a, c)] };
        var overlapped = BulkSelection.Create(overlap, Visible(a, b, c), KeepCopyPolicy.LargestFile);
        Check(overlapped.SelectedPaths.Count == 1 && DeletionService.ValidateSelection(overlap, overlapped.SelectedPaths) == null,
            "Overlapping groups retain additional copies when necessary");
        Check(overlap.Groups.All(g => g.Members.Any(m => !overlapped.SelectedPaths.Contains(m.File.Path))), "Every overlap retains at least one unchecked path");
        var unusual = new ScanReport { Groups = [Group("ab", a, b), Group("single", b)] };
        Check(BulkSelection.Create(unusual, Visible(a, b), KeepCopyPolicy.LargestFile).SelectedPaths.Count == 0,
            "The safety pass protects other groups outside the bulk-selection scope");
        var repeated = new ScanReport { Groups = [Group("repeated", a, a)] };
        Check(BulkSelection.Create(repeated, Visible(a), KeepCopyPolicy.LargestFile).Groups == 0,
            "Repeated rows of one physical path do not count as separate copies");
        var upperCase = new HashSet<string>(Visible(a, b).Select(p => p.ToUpperInvariant()));
        Check(BulkSelection.Create(report, upperCase, KeepCopyPolicy.LargestFile).SelectedPaths.SetEquals(Visible(b)),
            "Bulk selection compares Windows paths case-insensitively");
        Check(largest.SelectedPaths.All(p => Visible(a, b, c).Contains(p)), "Selection is limited to eligible paths from the report");
        var large = new ScanReport();
        for (int i = 0; i < 50000; i++) large.Groups.Add(Group(i.ToString(), File($"{i}-large", 200), File($"{i}-small", 100)));
        var all = large.Groups.SelectMany(g => g.Members).Select(m => m.File.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clock = Stopwatch.StartNew(); var result = BulkSelection.Create(large, all, KeepCopyPolicy.LargestFile); clock.Stop();
        Check(result.SelectedPaths.Count == 50000 && result.CopiesKept == 50000 && clock.Elapsed < TimeSpan.FromSeconds(5),
            "Bulk selection handles 100,000 result paths without pairwise comparisons");
        clock.Restart(); string? invalid = DeletionService.ValidateSelection(large, result.SelectedPaths); clock.Stop();
        Check(invalid == null && clock.Elapsed < TimeSpan.FromSeconds(3), "Review validation for 50,000 selected copies uses indexed path lookups");
        Check(DeletionService.ValidateSelection(report, new HashSet<string> { @"C:\bulk\outside.mkv" }) != null,
            "Indexed review validation rejects files outside the report");
        Check(DeletionService.ValidateSelection(report, new HashSet<string>(Visible(a, b, c).Select(p => p.ToUpperInvariant()))) != null,
            "Retained-copy validation is case-insensitive even for case-sensitive input sets");
    }
    private static async Task DeletionChecks(string run)
    {
        string directory = Path.Combine(run, "deletion"); Directory.CreateDirectory(directory);
        FileRecord Create(string name) { string path = Path.Combine(directory, name); File.WriteAllText(path, "Disposable duplicate fixture"); return Record(path); }
        var keep = Create("keep.mkv"); var remove = Create("remove.mkv");
        var group = new MatchGroup { Id = "delete-test", Kind = MatchKind.IdenticalContents, Members = [new(keep, new(100, "fixture")), new(remove, new(100, "fixture"))] };
        var report = new ScanReport { Groups = [group] };
        HashSet<string> Select(params FileRecord[] records) => records.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(DeletionService.ValidateSelection(report, Select(keep, remove)) != null, "Deleting every member is blocked");
        Check(DeletionService.ValidateSelection(report, Select(remove)) == null, "Keeping one copy permits reviewed selection");
        var fake = new FakeBackend(); var service = new DeletionService(fake);
        Check(service.Execute(report, Select(remove), false).Single().State == DeleteState.RequiresPermanentConfirmation && File.Exists(remove.Path) && fake.PermanentCalls == 0,
            "Declined permanent fallback never deletes");
        File.SetLastWriteTimeUtc(keep.Path, DateTime.UtcNow.AddMinutes(1));
        Check(service.Execute(report, Select(remove), true).Single().State == DeleteState.Skipped && File.Exists(remove.Path), "Changed retained copy blocks deletion");
        keep = Record(keep.Path); group.Members[0] = new(keep, new(100, "fixture"));
        File.SetLastWriteTimeUtc(remove.Path, DateTime.UtcNow.AddMinutes(1));
        Check(service.Execute(report, Select(remove), true).Single().State == DeleteState.Skipped && File.Exists(remove.Path), "Changed selected copy blocks deletion");
        remove = Record(remove.Path); group.Members[1] = new(remove, new(100, "fixture"));
        Check(service.Execute(report, Select(remove), true).Single().State == DeleteState.DeletedPermanently && !File.Exists(remove.Path), "Explicit permanent confirmation permits only selected fixture deletion");
        var first = Create("failure.mkv"); var second = Create("success.mkv");
        group.Members.Add(new(first, new(100, "fixture"))); group.Members.Add(new(second, new(100, "fixture")));
        fake.FailPath = first.Path;
        var outcomes = service.Execute(report, Select(first, second), true);
        Check(outcomes.Any(o => o.State == DeleteState.Failed) && outcomes.Any(o => o.State == DeleteState.DeletedPermanently) && File.Exists(first.Path), "Partial deletion failures are recorded per file");
        var overlapping = new MatchGroup { Id = "overlap", Kind = MatchKind.IdenticalFilename, Members = [new(keep, new(100, "")), new(first, new(100, ""))] };
        report.Groups.Add(overlapping);
        Check(DeletionService.ValidateSelection(report, Select(keep, first)) != null, "Retained-copy protection applies across overlapping search groups");
        if (OperatingSystem.IsWindows())
        {
            var guarded = Create("guarded-no-recycle.mkv");
            var guardType = typeof(ShellDeletionBackend).GetNestedType("RecycleGuard", System.Reflection.BindingFlags.NonPublic)!;
            var guard = Activator.CreateInstance(guardType, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, [guarded], null)!;
            int aborted = (int)guardType.GetMethod("PreDeleteItem")!.Invoke(guard, [0U, null])!;
            Check(aborted < 0 && (bool)guardType.GetField("NonRecyclable")!.GetValue(guard)! && File.Exists(guarded.Path),
                "Native callback vetoes permanent fallback before deletion");
            var recycle = Create("recycle-native.mkv");
            var outcome = await StaTask.Run(() => new ShellDeletionBackend().TryRecycle(recycle));
            Console.WriteLine("Native recycle outcome: " + JsonSerializer.Serialize(outcome));
            if (outcome.State == DeleteState.RequiresPermanentConfirmation)
                Check(File.Exists(recycle.Path), "Windows recycling unavailable in this environment: guarded fallback preserves the disposable file");
            else Check(outcome.State == DeleteState.Recycled && !File.Exists(recycle.Path), "Native Windows shell recycling works on disposable fixture");
            var permanent = Create("permanent-native.mkv");
            var native = new ShellDeletionBackend().DeletePermanently(permanent);
            Check(native.State == DeleteState.DeletedPermanently && !File.Exists(permanent.Path), "Native permanent backend deletes disposable fixture");
        }
    }
    private sealed class DirectProgress(Action<ScanProgress> action) : IProgress<ScanProgress> { public void Report(ScanProgress value) => action(value); }
    private sealed class FakeBackend : IDeletionBackend
    {
        public int PermanentCalls, RecycleCalls; public string? FailPath;
        public DeleteOutcome TryRecycle(FileRecord file) { RecycleCalls++; return new(file.Path, DeleteState.RequiresPermanentConfirmation, "Fixture cannot be recycled"); }
        public DeleteOutcome DeletePermanently(FileRecord file)
        {
            if (file.Path == FailPath) throw new IOException("Simulated failure");
            PermanentCalls++; File.Delete(file.Path); return new(file.Path, DeleteState.DeletedPermanently, "Fixture deleted");
        }
    }
}
