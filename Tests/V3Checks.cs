using SimilarVideoFinder.Core;
namespace SimilarVideoFinder.Tests;
internal static partial class Program
{
    private static async Task V3Checks(string run)
    {
        string folder = Path.Combine(run, "v3"); Directory.CreateDirectory(folder);
        FileRecord Make(string name) { string path = Path.Combine(folder, name); File.WriteAllText(path, "Disposable V3 fixture"); return Record(path); }
        HashSet<string> Select(params FileRecord[] files) => files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        MatchGroup Group(MatchKind kind, params FileRecord[] files) => new() { Id = "v3", Kind = kind, Members = files.Select(f => new MatchMember(f, new(100, "fixture"))).ToList() };
        var a = Make("one two three four five six seven eight nine.mkv");
        var b = Make("one two three four five six seven eight ten.mkv");
        var c = Make("one two three four five six seven nine eleven.mkv");
        var report = new ScanReport { NameThreshold = 85, Groups = [Group(MatchKind.SimilarFilename, a, b, c)] };
        Check(Names(a.Name, b.Name, 85) != null && Names(a.Name, c.Name, 85) != null && Names(b.Name, c.Name, 85) == null, "Non-transitive deletion fixture has two direct matches only");
        Check(DeletionService.ValidateSelection(report, Select(a, c)) != null, "Keeping B cannot justify deletion of unrelated C");
        Check(DeletionService.ValidateSelection(report, Select(b, c)) == null, "Keeping representative A provides direct witnesses");
        Check(DeletionService.ValidateSelection(report, Select(a, b, c)) != null, "Normal selection cannot delete every copy");
        Check(DeletionService.ValidateSelection(report, Select(a, b, c), Select(a, b, c)) == null, "Explicit whole-group discard permits every member");
        Check(DeletionService.ValidateSelection(report, Select(a, b, c), Select(a)) != null, "Discard override applies only to explicitly authorized paths");
        b.IsReference = true;
        Check(DeletionService.ValidateSelection(report, Select(a, b, c), Select(a, b, c)) != null, "Whole-group discard cannot override protected references"); b.IsReference = false;
        var bulk = BulkSelection.Create(report, Select(a, b, c), KeepCopyPolicy.FirstResult);
        Check(DeletionService.ValidateSelection(report, bulk.SelectedPaths) == null, "Bulk selection retains direct witnesses");
        var exact = new ScanReport { Groups = [Group(MatchKind.IdenticalContents, a, b)] };
        var backend = new FakeBackend(); var service = new DeletionService(backend, _ => false);
        var cancelled = new CancellationToken(true);
        Check(service.Execute(exact, Select(a, b), true, cancellation: cancelled, discardPaths: Select(a, b)).All(o => o.State == DeleteState.Skipped) && File.Exists(a.Path), "Cancellation preserves all unstarted files");
        int progress = 0;
        var outcomes = service.Execute(exact, Select(a, b), true, discardPaths: Select(a, b), progress: (done, total) => progress = done);
        Check(outcomes.All(o => o.State == DeleteState.DeletedPermanently) && progress == 2 && !File.Exists(a.Path) && !File.Exists(b.Path), "Whole-group execution deletes all disposable files and reports progress");
        var keep = Make("keep.mkv"); var remove = Make("remove.mkv");
        exact.Groups = [Group(MatchKind.IdenticalContents, keep, remove)];
        string quarantine = Path.Combine(folder, "quarantine");
        var q = new DeletionService(new QuarantineBackend(quarantine), _ => false);
        var quarantined = q.Execute(exact, Select(remove), false).Single();
        Check(quarantined.State == DeleteState.Quarantined && !File.Exists(remove.Path) && File.Exists(keep.Path), "Quarantine verifies a copy and removes only the selected original");
        string manifest = Directory.GetFiles(quarantine, "*.restore.json").Single();
        QuarantineBackend.Restore(manifest);
        Check(remove.IsCurrent() && File.ReadAllText(remove.Path) == "Disposable V3 fixture", "Quarantine restore recovers contents and timestamp");
        bool refused = false; try { QuarantineBackend.Restore(manifest); } catch (IOException) { refused = true; }
        Check(refused, "Restore never overwrites an existing path");
        using var cache = new CacheStore(Path.Combine(folder, "cache.sqlite"));
        cache.SaveReview(keep, remove, "Keep both");
        Check(cache.Review(remove, keep) == "Keep both", "Pair decisions are symmetric and available within the session");
        var changed = new FileRecord { Path = remove.Path, Size = remove.Size + 1, ModifiedTicks = remove.ModifiedTicks };
        Check(cache.Review(keep, changed) == null, "Pair review is invalidated by file identity changes");
        cache.Save(keep); Check(cache.Load(keep.Path, keep.Size, keep.ModifiedTicks) != null && cache.CacheHits == 1, "Cache hit statistics count successful reuse");
        for (int i = 0; i < 35; i++) { var r = new ScanReport { ScanId = cache.BeginScan(new()) }; cache.FinishScan(r); }
        Check(cache.Statistics().Scans == 30, "History retention actually removes older scans");
        var opt = new ScanOptions { Roots = [folder], ExcludedPatterns = "remove*;*eleven*", ExcludedFolders = [quarantine], MinimumSizeBytes = 1 };
        var inventory = FileInventory.Enumerate(opt, cache, _ => { }, _ => { }, CancellationToken.None);
        Check(inventory.Count == 1 && inventory[0].Name == "keep.mkv", "Exclusions prune folder trees and wildcard names before processing");
        File.Delete(keep.Path); Check(cache.RemoveStaleFiles() >= 1, "Stale cache records can be removed");
        cache.ClearHistory(); Check(cache.Statistics() == (0L, 0L, 0L, cache.Statistics().Bytes), "Clear history removes cached files scans and reviews");
        var source = Make("tracks.mkv"); var target = Make("target.mkv");
        source.Media = new(100, 3840, 2160, "hevc", "aac") { AudioTracks = [new("aac", "eng", "Commentary", 2)], SubtitleTracks = [new("srt", "fra", "")], DynamicRange = "HDR (PQ)" };
        target.Media = new(100, 1920, 1080, "h264", "aac") { AudioTracks = [new("aac", "eng", "", 2)], DynamicRange = "SDR" };
        var warnings = ReviewAnalysis.LossWarnings(source, [target]);
        Check(warnings.Count == 3, "Review warns about losing commentary subtitles and HDR");
        exact.Groups = [Group(MatchKind.IdenticalContents, source, target), Group(MatchKind.VideoContent, source, target)];
        Check(ReviewAnalysis.Evidence(exact, source, target).Count == 1, "Combined evidence reports only direct supported comparisons");
        Check(!BulkSelection.Create(exact, Select(source, target), KeepCopyPolicy.HighestResolution).SelectedPaths.Contains(source.Path), "Keep rules prefer higher resolution");
        exact.Groups.RemoveAt(1);
        Check(BulkSelection.Create(exact, Select(source, target), KeepCopyPolicy.PreferredCodec, new(Codec: "h264")).SelectedPaths.Contains(source.Path), "Keep rules prefer configured codec");
        Check(BulkSelection.Create(exact, Select(source, target), KeepCopyPolicy.MostSubtitles).SelectedPaths.Contains(target.Path), "Keep rules prefer subtitle availability");
        var visual = new[] { source, target, c };
        for (int i = 0; i < visual.Length; i++) { visual[i].Media = new(100, 10, 10, "h264", ""); visual[i].Frames = Enumerable.Range(0, 12).Select(n => new FrameFingerprint(n, i == 0 ? 0UL : i == 1 ? 3UL : 15UL, true)).ToList(); }
        var groups = VideoMatcher.Find(visual, 2);
        Check(groups.Count == 2 && groups.Any(g => g.Members.Any(m => m.File == target) && g.Members.Any(m => m.File == c)), "Visual index includes nonrepresentatives and preserves B-C edge");
        await Task.CompletedTask;
    }
}
