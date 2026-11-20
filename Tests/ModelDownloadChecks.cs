using MediaForge.SubtitleModels;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimilarVideoFinder.Tests;

internal static partial class Program
{
    private sealed class ModelFixtureHandler : HttpMessageHandler
    {
        public const string Revision = "1234567890123456789012345678901234567890";
        public Dictionary<string, byte[]> Files { get; } = new()
        {
            ["config.json"] = Encoding.UTF8.GetBytes("{\"is_multilingual\":true}"),
            ["tokenizer.json"] = Encoding.UTF8.GetBytes("{\"version\":\"1.0\"}"),
            ["preprocessor_config.json"] = Encoding.UTF8.GetBytes("{\"feature_size\":80}"),
            ["vocabulary.txt"] = Encoding.UTF8.GetBytes("test vocabulary"),
            ["model.bin"] = Enumerable.Range(0, 2 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray()
        };
        public bool Corrupt { get; set; }
        public bool Truncate { get; set; }
        public bool Slow { get; set; }
        public bool UnsafeName { get; set; }
        public bool InvalidRevision { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request.RequestUri!);
            if (Status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(Status));
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/models/", StringComparison.Ordinal))
            {
                var siblings = Files.Select(pair =>
                {
                    string name = pair.Key == "model.bin" && UnsafeName ? "../model.bin" : pair.Key;
                    object value = pair.Key == "model.bin"
                        ? new { rfilename = name, size = (long)pair.Value.Length, blobId = new string('a', 40), lfs = new { size = (long)pair.Value.Length, sha256 = Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant() } }
                        : new { rfilename = name, size = (long)pair.Value.Length, blobId = GitBlobHash(pair.Value) };
                    return value;
                }).ToArray();
                string json = JsonSerializer.Serialize(new { sha = InvalidRevision ? "main" : Revision, siblings });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
            string file = Uri.UnescapeDataString(request.RequestUri.Segments.Last());
            byte[] bytes = Files[file].ToArray();
            if (file == "model.bin" && Corrupt) bytes[10] ^= 1;
            if (file == "model.bin" && Truncate) bytes = bytes[..128];
            HttpContent content = Slow && file == "model.bin" ? new StreamContent(new SlowModelStream(bytes)) : new ByteArrayContent(bytes);
            content.Headers.ContentLength = bytes.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
    private sealed class SlowModelStream(byte[] data) : MemoryStream(data, false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(25, cancellationToken); return await base.ReadAsync(buffer, cancellationToken); }
    }
    private sealed class InlineModelProgress(Action<ModelDownloadProgress> action) : IProgress<ModelDownloadProgress>
    { public void Report(ModelDownloadProgress value) => action(value); }
    private static string GitBlobHash(byte[] bytes)
    {
        byte[] header = Encoding.ASCII.GetBytes("blob " + bytes.Length + '\0');
        return Convert.ToHexString(SHA1.HashData(header.Concat(bytes).ToArray())).ToLowerInvariant();
    }
    private static async Task ModelDownloadChecks(string run)
    {
        string root = Path.Combine(run, "downloaded-models");
        var fixture = new ModelFixtureHandler();
        using var http = new HttpClient(fixture) { Timeout = Timeout.InfiniteTimeSpan };
        var downloader = new WhisperModelDownloader(http);
        var updates = new List<ModelDownloadProgress>();
        var installed = await downloader.DownloadAsync("small", root, new InlineModelProgress(updates.Add), CancellationToken.None);
        Check(installed.Manifest.Files.Count == 5 && fixture.Files.Keys.All(n => File.Exists(Path.Combine(installed.Directory, n))), "Models: required weights and optional vocabulary/preprocessor files are installed");
        Check(WhisperModels.FindInstalled(root, "small")?.Directory == installed.Directory, "Models: installed preset is discovered offline from the selected storage folder");
        Check(WhisperModels.FindInstalled(root, "medium") == null, "Models: presets never substitute another model size");
        Check(updates.First().Stage == "Reading model information" && updates.Last().Percent == 100 && updates.Last().ReceivedBytes == fixture.Files.Values.Sum(b => b.LongLength), "Models: download reports byte progress and completion");
        Check(fixture.Requests.Where(u => !u.AbsolutePath.StartsWith("/api/")).All(u => u.AbsolutePath.Contains(ModelFixtureHandler.Revision)), "Models: every asset download is pinned to the metadata commit");
        Check(File.ReadAllBytes(Path.Combine(installed.Directory, "model.bin")).SequenceEqual(fixture.Files["model.bin"]), "Models: downloaded weights match the verified bytes");
        Check(!Directory.EnumerateDirectories(root, ".download-*").Any(), "Models: no staging directories remain after success");
        int requests = fixture.Requests.Count;
        var reused = await downloader.DownloadAsync("small", root, null, CancellationToken.None);
        Check(reused.Directory == installed.Directory && fixture.Requests.Count == requests + 1, "Models: explicit download verifies and reuses a complete current model");
        byte[] modified = fixture.Files["model.bin"].ToArray(); modified[10] ^= 1;
        File.WriteAllBytes(Path.Combine(installed.Directory, "model.bin"), modified);
        var repaired = await downloader.DownloadAsync("small", root, null, CancellationToken.None);
        Check(repaired.Directory != installed.Directory && File.ReadAllBytes(Path.Combine(repaired.Directory, "model.bin")).SequenceEqual(fixture.Files["model.bin"]), "Models: a corrupted installed model is downloaded into a fresh verified folder");
        Check(File.ReadAllBytes(Path.Combine(installed.Directory, "model.bin")).SequenceEqual(modified), "Models: repair does not overwrite an existing model folder");

        async Task<bool> Fails(ModelFixtureHandler handler, string name, CancellationToken token = default)
        {
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            try { await new WhisperModelDownloader(client).DownloadAsync("medium", Path.Combine(run, name), null, token); return false; }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException) { return true; }
        }
        Check(await Fails(new() { Corrupt = true }, "bad-hash"), "Models: invalid SHA-256 weights are rejected");
        Check(WhisperModels.FindInstalled(Path.Combine(run, "bad-hash"), "medium") == null && !Directory.EnumerateDirectories(Path.Combine(run, "bad-hash")).Any(), "Models: verification failure leaves no ready model or partial download");
        Check(await Fails(new() { Truncate = true }, "truncated-download"), "Models: a truncated download is rejected");
        Check(await Fails(new() { UnsafeName = true }, "unsafe-metadata"), "Models: path traversal metadata cannot replace the required weights");
        Check(await Fails(new() { InvalidRevision = true }, "invalid-revision"), "Models: unpinned model metadata is rejected");
        Check(await Fails(new() { Status = HttpStatusCode.TooManyRequests }, "rate-limited"), "Models: model-server HTTP failures are reported");
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        Check(await Fails(new() { Slow = true }, "cancelled-download", stop.Token), "Models: an active transfer can be cancelled");
        Check(!Directory.EnumerateDirectories(Path.Combine(run, "cancelled-download")).Any(), "Models: cancellation removes its incomplete staging directory");
        Check(WhisperModels.FindInstalled(root, "small") != null, "Models: a cancelled transfer does not disturb a previously installed preset");
        bool rejected = false;
        try { await downloader.DownloadAsync("../unsafe", root, null, CancellationToken.None); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Models: only supported multilingual presets can be downloaded");
        string malformed = Path.Combine(root, "small-malformed"); Directory.CreateDirectory(malformed);
        File.WriteAllText(Path.Combine(malformed, "mediaforge-model.json"), "null");
        Check(WhisperModels.FindInstalled(root, "small") != null, "Models: malformed manifests do not hide a complete installation");
        Check(WhisperModels.DefaultModelsDirectory(Path.Combine(run, "exe")) == Path.Combine(run, "exe", "MediaForge-Data", "models")
            && WhisperModels.WorkDirectory(Path.Combine(run, "exe")) == Path.Combine(run, "exe", "MediaForge-Data", "work"), "Models: portable defaults are relative to the executable folder");
        string moved = Path.Combine(run, "moved-models"); Directory.Move(root, moved);
        Check(WhisperModels.FindInstalled(moved, "small") != null, "Models: downloaded model manifests remain valid after moving the portable folder");
    }

    private static async Task ModelDownloadLiveChecks()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var downloader = new WhisperModelDownloader(http);
        foreach (string preset in WhisperModels.Presets)
        {
            var plan = await downloader.GetDownloadPlanAsync(preset, CancellationToken.None);
            Check(plan.Files.Any(f => f.Name == "model.bin" && f.Algorithm == "sha256" && f.Size > 1_000_000), "Live models: verified weights metadata for " + preset);
            var config = plan.Files.Single(f => f.Name == "config.json");
            byte[] bytes = await http.GetByteArrayAsync($"https://huggingface.co/{plan.Repository}/resolve/{plan.Revision}/config.json?download=true");
            string hash = config.Algorithm == "git-sha1" ? GitBlobHash(bytes) : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Check(bytes.LongLength == config.Size && hash == config.Hash, "Live models: revision-pinned config download and checksum for " + preset);
        }
    }
}
