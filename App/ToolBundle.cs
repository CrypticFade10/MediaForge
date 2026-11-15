using SimilarVideoFinder.Core;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace SimilarVideoFinder.App;

public static class ToolBundle
{
    public static MediaProcessor Prepare(string dataDirectory)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var checksumsStream = assembly.GetManifestResourceStream("Tools.checksums.json") ?? throw new IOException("Bundled tool manifest is missing.");
        var checksums = JsonSerializer.Deserialize<Dictionary<string, string>>(checksumsStream)!;
        string directory = Path.Combine(dataDirectory, "tools", "ffmpeg-9.0.2-" + checksums["ffmpeg.exe"][..12]);
        Directory.CreateDirectory(directory);
        foreach (var item in checksums)
        {
            string path = Path.Combine(directory, item.Key);
            bool valid = false;
            if (File.Exists(path))
            {
                using var existing = File.OpenRead(path);
                valid = Convert.ToHexString(SHA256.HashData(existing)).Equals(item.Value, StringComparison.OrdinalIgnoreCase);
            }
            if (!valid)
            {
                using var resource = assembly.GetManifestResourceStream("Tools." + item.Key) ?? throw new IOException("Bundled video tool is missing.");
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var file = File.Create(temporary)) resource.CopyTo(file);
                    using (var file = File.OpenRead(temporary))
                        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(item.Value, StringComparison.OrdinalIgnoreCase)) throw new IOException("Bundled video tool checksum failed.");
                    File.Move(temporary, path, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        return new(Path.Combine(directory, "ffmpeg.exe"), Path.Combine(directory, "ffprobe.exe"));
    }
    public static string Notices()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Notices.All.txt")!;
        using var reader = new StreamReader(stream);
        using var license = Assembly.GetExecutingAssembly().GetManifestResourceStream("Notices.FFmpeg.txt")!;
        using var licenseReader = new StreamReader(license);
        return reader.ReadToEnd() + "\n\nFFmpeg licence\n" + licenseReader.ReadToEnd();
    }
}
