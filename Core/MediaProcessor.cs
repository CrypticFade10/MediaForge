using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace SimilarVideoFinder.Core;

public sealed class MediaProcessor(string ffmpeg, string ffprobe)
{
    private int fingerprintRuns;
    public int FingerprintRuns => Volatile.Read(ref fingerprintRuns);
    public string FFmpegPath => ffmpeg;
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellation)
    {
        byte[] data = await RunAsync(ffprobe, ["-v", "error", "-show_format", "-show_streams", "-of", "json", path], cancellation);
        using var json = JsonDocument.Parse(data);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => s.TryGetProperty("codec_type", out var t) && t.GetString() == "video"
            && !(s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.GetInt32() == 1));
        if (video.ValueKind == JsonValueKind.Undefined) throw new IOException("No decodable video stream.");
        double duration = 0;
        if (video.TryGetProperty("duration", out var vd)) double.TryParse(vd.GetString(), CultureInfo.InvariantCulture, out duration);
        if (duration <= 0 && json.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var fd))
            double.TryParse(fd.GetString(), CultureInfo.InvariantCulture, out duration);
        if (!double.IsFinite(duration) || duration <= 0) throw new IOException("Video duration is unavailable.");
        var audioTracks = streams.Where(s => String(s, "codec_type") == "audio").Select(Track).ToList();
        var subtitleTracks = streams.Where(s => String(s, "codec_type") == "subtitle").Select(Track).ToList();
        string audio = audioTracks.FirstOrDefault()?.Codec ?? "none";
        string pixelFormat = String(video, "pix_fmt");
        int depth = (int)Number(video, "bits_per_raw_sample");
        if (depth == 0)
        {
            var bits = System.Text.RegularExpressions.Regex.Match(pixelFormat, @"(?:p|gbrp|gray)(?<bits>9|10|12|14|16)(?:le|be)?$");
            depth = bits.Success ? int.Parse(bits.Groups["bits"].Value) : pixelFormat.Length > 0 ? 8 : 0;
        }
        string transfer = String(video, "color_transfer");
        string dynamicRange = transfer switch { "smpte2084" => "HDR (PQ)", "arib-std-b67" => "HDR (HLG)", "bt709" or "smpte170m" or "iec61966-2-1" => "SDR", _ => "Unknown" };
        if (video.TryGetProperty("side_data_list", out var side) && side.ToString().Contains("DOVI", StringComparison.OrdinalIgnoreCase)) dynamicRange = "Dolby Vision";
        long bitrate = Number(video, "bit_rate");
        if (bitrate == 0 && json.RootElement.TryGetProperty("format", out var container)) bitrate = Number(container, "bit_rate");
        return new(duration, video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32(),
            String(video, "codec_name", "unknown"), audio)
        { AudioTracks = audioTracks, SubtitleTracks = subtitleTracks, BitDepth = depth, PixelFormat = pixelFormat, DynamicRange = dynamicRange, Bitrate = bitrate };
    }
    private static string String(JsonElement element, string key, string fallback = "") => element.TryGetProperty(key, out var v) ? v.ToString() : fallback;
    private static long Number(JsonElement element, string key) => long.TryParse(String(element, key), out var n) ? n : 0;
    private static MediaTrack Track(JsonElement stream)
    {
        string language = "und", title = "";
        if (stream.TryGetProperty("tags", out var tags)) { language = String(tags, "language", "und"); title = String(tags, "title"); }
        bool isDefault = stream.TryGetProperty("disposition", out var disposition) && Number(disposition, "default") == 1;
        return new(String(stream, "codec_name", "unknown"), language, title, (int)Number(stream, "channels"), isDefault);
    }
    public async Task<byte[]> FrameAsync(string path, double seconds, CancellationToken cancellation)
    {
        return await RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-threads", "1", "-ss", seconds.ToString("0.######", CultureInfo.InvariantCulture),
            "-i", path, "-map", "0:V:0", "-frames:v", "1", "-an", "-sn", "-vf", "scale=160:90:flags=area,format=gray",
            "-threads", "1", "-f", "rawvideo", "pipe:1"], cancellation);
    }
    public async Task<List<FrameFingerprint>> FingerprintAsync(FileRecord file, CancellationToken cancellation)
    {
        if (file.Media == null) throw new InvalidOperationException("Probe video metadata first.");
        Interlocked.Increment(ref fingerprintRuns);
        // One process performs the same twelve accurate seeks. No full-video decoding is required.
        var arguments = new List<string> { "-v", "error", "-nostdin", "-filter_complex_threads", "1" };
        var filters = new List<string>();
        for (int i = 0; i < 12; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            double seconds = file.Media.Duration * Position(i);
            arguments.AddRange(["-threads", "1", "-ss", seconds.ToString("0.######", CultureInfo.InvariantCulture), "-i", file.Path]);
            filters.Add($"[{i}:V:0]trim=end_frame=1,setpts=PTS-STARTPTS,scale=160:90:flags=area,setsar=1,format=gray[f{i}]");
        }
        filters.Add(string.Concat(Enumerable.Range(0, 12).Select(i => $"[f{i}]")) + "concat=n=12:v=1:a=0[out]");
        arguments.AddRange(["-filter_complex", string.Join(';', filters), "-map", "[out]", "-frames:v", "12", "-fps_mode", "passthrough", "-threads", "1", "-f", "rawvideo", "pipe:1"]);
        byte[] images = await RunAsync(ffmpeg, arguments, cancellation, 180);
        if (images.Length != 12 * 160 * 90) throw new IOException("Not all twelve video samples could be decoded.");
        var frames = new List<FrameFingerprint>(12);
        for (int i = 0; i < 12; i++) { cancellation.ThrowIfCancellationRequested(); frames.Add(PerceptualHash.Compute(images.AsSpan(i * 160 * 90, 160 * 90).ToArray(), 160, 90, i)); }
        return frames;
    }
    public Task<byte[]> PreviewAsync(string path, double seconds, CancellationToken cancellation) => RunAsync(ffmpeg,
        ["-v", "error", "-nostdin", "-threads", "1", "-ss", seconds.ToString("0.######", CultureInfo.InvariantCulture), "-i", path,
         "-map", "0:V:0", "-frames:v", "1", "-an", "-sn", "-vf", "scale=960:540:force_original_aspect_ratio=decrease", "-threads", "1", "-c:v", "png", "-f", "image2pipe", "pipe:1"], cancellation);
    public static double Position(int index) => 0.1 + 0.8 * index / 11;
    public static async Task<byte[]> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellation, int timeoutSeconds = 90)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("Unable to start video tools.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var registration = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await Task.WhenAll(copy, process.WaitForExitAsync(timeout.Token));
            string stderr = await errors;
            if (process.ExitCode != 0) throw new IOException("Video tool failed: " + stderr[..Math.Min(stderr.Length, 600)]);
            return output.ToArray();
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            try { await process.WaitForExitAsync(); } catch { }
            // Observe both readers so cancellation cannot leave background pipe tasks behind.
            try { await copy; } catch { }
            try { await errors; } catch { }
            cancellation.ThrowIfCancellationRequested();
            throw new TimeoutException($"Video tool exceeded {timeoutSeconds} seconds.");
        }
    }
}

public static class PerceptualHash
{
    private static readonly double[,] Cosines = MakeCosines();
    private static double[,] MakeCosines()
    {
        var result = new double[8, 32];
        for (int u = 0; u < 8; u++) for (int x = 0; x < 32; x++) result[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / 64);
        return result;
    }
    public static FrameFingerprint Compute(byte[] pixels, int width, int height, int index)
    {
        if (pixels.Length != width * height) throw new IOException("Video frame could not be decoded.");
        int top = 0, bottom = height - 1, left = 0, right = width - 1;
        bool DarkRow(int y) { long sum = 0; for (int x = 0; x < width; x++) sum += pixels[y * width + x]; return sum / (double)width < 18; }
        bool DarkColumn(int x) { long sum = 0; for (int y = top; y <= bottom; y++) sum += pixels[y * width + x]; return sum / (double)(bottom - top + 1) < 18; }
        while (top < height / 4 && DarkRow(top)) top++;
        while (bottom >= 3 * height / 4 && DarkRow(bottom)) bottom--;
        while (left < width / 4 && DarkColumn(left)) left++;
        while (right >= 3 * width / 4 && DarkColumn(right)) right--;
        var resized = new double[32, 32];
        double sum = 0, square = 0;
        // Area averaging avoids aliasing when shrinking detailed or differently encoded frames.
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
        {
            int x0 = left + x * (right - left + 1) / 32;
            int x1 = Math.Max(x0 + 1, left + (x + 1) * (right - left + 1) / 32);
            int y0 = top + y * (bottom - top + 1) / 32;
            int y1 = Math.Max(y0 + 1, top + (y + 1) * (bottom - top + 1) / 32);
            double value = 0; int n = 0;
            for (int yy = y0; yy < y1; yy++) for (int xx = x0; xx < x1; xx++) { value += pixels[yy * width + xx]; n++; }
            value /= n; resized[y, x] = value; sum += value; square += value * value;
        }
        bool informative = square / 1024 - Math.Pow(sum / 1024, 2) >= 36;
        var intermediate = new double[32, 8];
        for (int y = 0; y < 32; y++) for (int u = 0; u < 8; u++)
            for (int x = 0; x < 32; x++) intermediate[y, u] += resized[y, x] * Cosines[u, x];
        var dct = new double[64];
        for (int v = 0; v < 8; v++) for (int u = 0; u < 8; u++)
            for (int y = 0; y < 32; y++) dct[v * 8 + u] += intermediate[y, u] * Cosines[v, y];
        double median = dct.Skip(1).Order().ElementAt(31);
        ulong hash = 0;
        for (int bit = 1; bit < 64; bit++) if (dct[bit] > median) hash |= 1UL << bit;
        return new(index, hash, informative);
    }
    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}
