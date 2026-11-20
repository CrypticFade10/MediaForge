using System.Text.Json;

namespace MediaForge.SubtitleModels;

public sealed record ModelDownloadProgress(string Stage, string File, long ReceivedBytes, long TotalBytes)
{
    public double? Percent => TotalBytes > 0 ? Math.Clamp(100d * ReceivedBytes / TotalBytes, 0, 100) : null;
}
public sealed record ModelFile(string Name, long Size, string Hash, string Algorithm);
public sealed record ModelManifest(string Preset, string Repository, string Revision, DateTime InstalledUtc, List<ModelFile> Files);
public sealed record InstalledModel(string Directory, ModelManifest Manifest);

public static class WhisperModels
{
    public static IReadOnlyList<string> Presets { get; } = Array.AsReadOnly(new[] { "small", "medium", "large-v3" });
    public static string Repository(string preset) => Presets.Contains(preset, StringComparer.Ordinal)
        ? "Systran/faster-whisper-" + preset : throw new ArgumentException("Choose small, medium or large-v3.", nameof(preset));
    public static string DefaultModelsDirectory(string executableDirectory) => Path.Combine(executableDirectory, "MediaForge-Data", "models");
    public static string WorkDirectory(string executableDirectory) => Path.Combine(executableDirectory, "MediaForge-Data", "work");
    internal static bool AllowedFile(string name) => name is "model.bin" or "config.json" or "tokenizer.json" or "preprocessor_config.json" or "vocabulary.json" or "vocabulary.txt";
    internal const string ManifestName = "mediaforge-model.json";
    internal static bool IsHex(string value, int length) => value.Length == length && value.All(char.IsAsciiHexDigit);
    internal static bool ValidManifest(ModelManifest m, string preset)
    {
        if (m.Preset != preset || m.Repository != Repository(preset) || !IsHex(m.Revision, 40) || m.Files is not { Count: >= 3 and <= 6 }) return false;
        if (m.Files.Any(f => !AllowedFile(f.Name) || f.Size <= 0 || !(f.Algorithm == "sha256" && IsHex(f.Hash, 64) || f.Algorithm == "git-sha1" && IsHex(f.Hash, 40)))) return false;
        return m.Files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == m.Files.Count
            && new[] { "model.bin", "config.json", "tokenizer.json" }.All(n => m.Files.Any(f => f.Name == n));
    }

    public static InstalledModel? FindInstalled(string modelsDirectory, string preset)
    {
        _ = Repository(preset);
        if (!System.IO.Directory.Exists(modelsDirectory)) return null;
        var found = new List<InstalledModel>();
        foreach (string directory in System.IO.Directory.EnumerateDirectories(modelsDirectory, preset + "-*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                string manifestPath = Path.Combine(directory, ManifestName);
                if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 65536) continue;
                var manifest = JsonSerializer.Deserialize<ModelManifest>(File.ReadAllText(manifestPath));
                if (manifest == null || !ValidManifest(manifest, preset)) continue;
                if (manifest.Files.All(f => File.Exists(Path.Combine(directory, f.Name)) && new FileInfo(Path.Combine(directory, f.Name)).Length == f.Size))
                    found.Add(new(Path.GetFullPath(directory), manifest));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException) { }
        }
        return found.OrderByDescending(f => f.Manifest.InstalledUtc).FirstOrDefault();
    }
}
