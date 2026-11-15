using SimilarVideoFinder.Core;
namespace SimilarVideoFinder.Tests;
internal static partial class Program
{
    private static async Task NasChecks(string root, string workspace)
    {
        if (!DeletionPolicy.IsNetworkPath(root)) throw new ArgumentException("NAS fixture requires a UNC share or mapped network drive.");
        string fixture = Path.Combine(Path.GetFullPath(root), "SimilarVideoFinder-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        Console.WriteLine("Disposable NAS fixture: " + fixture);
        byte[] data = new byte[8 * 1024 * 1024]; new Random(51).NextBytes(data);
        string a = Path.Combine(fixture, "keep.mkv"), b = Path.Combine(fixture, "remove.mkv");
        await File.WriteAllBytesAsync(a, data); await File.WriteAllBytesAsync(b, data);
        using var cache = new CacheStore(Path.Combine(workspace, "artifacts", "nas.sqlite"));
        var media = new MediaProcessor(Path.Combine(workspace, "vendor", "ffmpeg.exe"), Path.Combine(workspace, "vendor", "ffprobe.exe"));
        var options = new ScanOptions { Roots = [fixture], SimilarFilename = false, EpisodeInFolder = false, VideoContent = false };
        var report = await new ScanEngine(cache, media).ScanAsync(options, null, CancellationToken.None);
        var group = report.Groups.Single(g => g.Kind == MatchKind.IdenticalContents);
        Check(group.Members.Count == 2, "Real NAS reads confirm complete identical-content hashes");
        var selected = group.Members.Where(m => m.File.Name == "remove.mkv").Select(m => m.File.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var backend = new FakeBackend(); var service = new DeletionService(backend);
        Check(service.Execute(report, selected, false).Single().State == DeleteState.RequiresPermanentConfirmation && backend.RecycleCalls == 0, "Real network paths require explicit permanent confirmation and skip recycling");
        File.Move(a, a + ".unavailable");
        Check(service.Execute(report, selected, true).Single().State == DeleteState.Skipped && File.Exists(b), "Unavailable NAS retained copy blocks deletion");
        File.Move(a + ".unavailable", a);
        Check(service.Execute(report, selected, true).Single().State == DeleteState.DeletedPermanently && File.Exists(a), "Confirmed NAS deletion removes only the disposable selected file");
        Console.WriteLine("NAS checks passed. Retained fixture remains at: " + fixture + ". Physical disconnection and mapped/UNC alias tests still require a controlled live-share session.");
    }
}
