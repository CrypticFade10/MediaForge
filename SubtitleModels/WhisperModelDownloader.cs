using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MediaForge.SubtitleModels;

/// <summary>Downloads only known CTranslate2 model assets, pinned to one Hub revision.</summary>
public sealed class WhisperModelDownloader(HttpClient http)
{
    public Task<ModelManifest> GetDownloadPlanAsync(string preset, CancellationToken cancellation) => ReadPlanAsync(preset, WhisperModels.Repository(preset), cancellation);
    public async Task<InstalledModel> DownloadAsync(string preset, string modelsDirectory, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellation)
    {
        string repository = WhisperModels.Repository(preset);
        string root = Path.GetFullPath(modelsDirectory);
        Directory.CreateDirectory(root);
        // Detect unwritable portable storage before starting any large transfer.
        string staging = Path.Combine(root, ".download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            progress?.Report(new("Reading model information", preset, 0, 0));
            var plan = await ReadPlanAsync(preset, repository, cancellation);
            long total = checked(plan.Files.Sum(f => f.Size));
            var existing = WhisperModels.FindInstalled(root, preset);
            if (existing?.Manifest.Revision == plan.Revision && await VerifyAsync(existing.Directory, plan.Files, progress, cancellation))
            {
                progress?.Report(new("Model already downloaded and verified", preset, total, total));
                return existing;
            }

            long received = 0;
            foreach (var asset in plan.Files)
            {
                cancellation.ThrowIfCancellationRequested();
                progress?.Report(new("Downloading", asset.Name, received, total));
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://huggingface.co/{repository}/resolve/{plan.Revision}/{Uri.EscapeDataString(asset.Name)}?download=true");
                request.Headers.UserAgent.ParseAdd("MediaForge/3.3.1");
                using var response = await SendAsync(request, cancellation);
                if (response.Content.Headers.ContentLength is long length && length != asset.Size)
                    throw new IOException($"Unexpected download size for {asset.Name}. Retry the model download.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellation);
                string path = Path.Combine(staging, asset.Name);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);
                using var hash = NewHash(asset);
                long fileBytes = 0;
                byte[] buffer = new byte[131072];
                var throttle = Stopwatch.StartNew();
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    idle.CancelAfter(TimeSpan.FromMinutes(2));
                    int count;
                    try { count = await input.ReadAsync(buffer.AsMemory(), idle.Token); }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                    { throw new IOException("The model download stopped responding. Check your connection and retry."); }
                    if (count == 0) break;
                    if (fileBytes + count > asset.Size) throw new IOException($"Unexpected data in {asset.Name}; the model was not installed.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                    hash.AppendData(buffer, 0, count); fileBytes += count; received += count;
                    if (throttle.ElapsedMilliseconds >= 100)
                    { progress?.Report(new("Downloading", asset.Name, received, total)); throttle.Restart(); }
                }
                await output.FlushAsync(cancellation);
                if (fileBytes != asset.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Verification failed for {asset.Name}. The incomplete model was not installed; retry the download.");
            }

            cancellation.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(Path.Combine(staging, WhisperModels.ManifestName), JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }), cancellation);
            // Existing models, including manually copied folders, are never overwritten.
            string target = Path.Combine(root, preset + "-" + plan.Revision[..12] + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.Move(staging, target);
            progress?.Report(new("Model ready", preset, total, total));
            return new(target, plan);
        }
        finally
        {
            // Only this operation's newly-created staging directory is removed.
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<ModelManifest> ReadPlanAsync(string preset, string repository, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://huggingface.co/api/models/{repository}/revision/main?blobs=true");
        request.Headers.UserAgent.ParseAdd("MediaForge/3.3.1");
        using var response = await SendAsync(request, cancellation);
        // Metadata is small. Refuse unbounded responses before parsing JSON.
        using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        bodyTimeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, bodyTimeout.Token); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new IOException("The model information download stopped responding. Retry the download."); }
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellation));
        string revision = json.RootElement.GetProperty("sha").GetString() ?? "";
        var files = new List<ModelFile>();
        foreach (var item in json.RootElement.GetProperty("siblings").EnumerateArray())
        {
            string name = item.GetProperty("rfilename").GetString() ?? "";
            if (!WhisperModels.AllowedFile(name)) continue;
            if (item.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object)
                files.Add(new(name, lfs.GetProperty("size").GetInt64(), lfs.GetProperty("sha256").GetString() ?? "", "sha256"));
            else
                files.Add(new(name, item.GetProperty("size").GetInt64(), item.GetProperty("blobId").GetString() ?? "", "git-sha1"));
        }
        var manifest = new ModelManifest(preset, repository, revision, DateTime.UtcNow, files);
        if (!WhisperModels.ValidManifest(manifest, preset)) throw new IOException("The model server returned incomplete verification information. No model was installed.");
        return manifest;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new IOException("The model server did not respond. Check your connection and retry."); }
        if (!response.IsSuccessStatusCode)
        {
            HttpStatusCode status = response.StatusCode; response.Dispose();
            throw new HttpRequestException($"The model server returned HTTP {(int)status} ({status}). Check your connection or try again later.", null, status);
        }
        return response;
    }

    private static IncrementalHash NewHash(ModelFile file)
    {
        var hash = IncrementalHash.CreateHash(file.Algorithm == "sha256" ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
        if (file.Algorithm == "git-sha1") hash.AppendData(Encoding.ASCII.GetBytes("blob " + file.Size.ToString(System.Globalization.CultureInfo.InvariantCulture) + '\0'));
        return hash;
    }
    private static async Task<bool> VerifyAsync(string directory, List<ModelFile> files, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellation)
    {
        try { return await VerifyFilesAsync(directory, files, progress, cancellation); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private static async Task<bool> VerifyFilesAsync(string directory, List<ModelFile> files, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellation)
    {
        long done = 0, total = files.Sum(f => f.Size);
        foreach (var file in files)
        {
            progress?.Report(new("Verifying downloaded model", file.Name, done, total));
            await using var input = new FileStream(Path.Combine(directory, file.Name), FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);
            if (input.Length != file.Size) return false;
            using var hash = NewHash(file);
            byte[] buffer = new byte[131072];
            int count;
            while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
            { hash.AppendData(buffer, 0, count); done += count; }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Hash, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
