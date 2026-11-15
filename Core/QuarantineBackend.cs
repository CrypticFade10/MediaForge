using System.Security.Cryptography;
using System.Text.Json;
namespace SimilarVideoFinder.Core;

public sealed class QuarantineEntry
{
    public string OriginalPath { get; set; } = "";
    public string StoredPath { get; set; } = "";
    public long Size { get; set; }
    public long ModifiedTicks { get; set; }
    public string Hash { get; set; } = "";
    public bool Completed { get; set; }
    public bool Restored { get; set; }
}
public sealed class QuarantineBackend(string folder) : IDeletionBackend
{
    public DeleteOutcome TryRecycle(FileRecord file)
    {
        string root = System.IO.Path.GetFullPath(folder);
        if (ReferencePolicy.IsWithin(file.Path, root)) return new(file.Path, DeleteState.Failed, "Source is already within the quarantine folder.");
        Directory.CreateDirectory(root);
        string id = Guid.NewGuid().ToString("N");
        string stored = System.IO.Path.Combine(root, id + System.IO.Path.GetExtension(file.Path));
        string manifest = System.IO.Path.Combine(root, id + ".restore.json");
        using (var source = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (!file.IsCurrent()) return new(file.Path, DeleteState.Skipped, "Source changed before quarantine.");
            var entry = new QuarantineEntry { OriginalPath = file.Path, StoredPath = stored, Size = file.Size, ModifiedTicks = file.ModifiedTicks,
                Hash = Convert.ToHexString(SHA256.HashData(source)) };
            // Persist restoration information before creating or removing media.
            Save(manifest, entry);
            source.Position = 0;
            using (var destination = new FileStream(stored, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { source.CopyTo(destination); destination.Flush(true); }
            using var verify = File.OpenRead(stored);
            if (verify.Length != entry.Size || Convert.ToHexString(SHA256.HashData(verify)) != entry.Hash) throw new IOException("Quarantine verification failed; source retained.");
        }
        if (!file.IsCurrent()) return new(file.Path, DeleteState.Skipped, "Source changed; quarantine copy and manifest retained for review.");
        File.Delete(file.Path);
        var completed = JsonSerializer.Deserialize<QuarantineEntry>(File.ReadAllText(manifest))!;
        completed.Completed = true;
        try { Save(manifest, completed); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(file.Path, DeleteState.Quarantined, "Verified quarantine copy; restore manifest: " + manifest, "Unable to mark manifest complete: " + ex.Message); }
        return new(file.Path, DeleteState.Quarantined, "Verified quarantine copy; restore manifest: " + manifest);
    }
    public DeleteOutcome DeletePermanently(FileRecord file) => TryRecycle(file);
    public static string Restore(string manifest)
    {
        var entry = JsonSerializer.Deserialize<QuarantineEntry>(File.ReadAllText(manifest)) ?? throw new IOException("Invalid restoration manifest.");
        string root = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(manifest))!;
        if (!ReferencePolicy.IsWithin(entry.StoredPath, root) || !System.IO.Path.IsPathFullyQualified(entry.OriginalPath)) throw new IOException("Invalid restoration paths.");
        using var source = File.OpenRead(entry.StoredPath);
        if (source.Length != entry.Size || Convert.ToHexString(SHA256.HashData(source)) != entry.Hash) throw new IOException("Quarantine copy failed verification.");
        if (File.Exists(entry.OriginalPath)) throw new IOException("Original path already exists; nothing was overwritten.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(entry.OriginalPath)!);
        source.Position = 0;
        using (var destination = new FileStream(entry.OriginalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { source.CopyTo(destination); destination.Flush(true); }
        File.SetLastWriteTimeUtc(entry.OriginalPath, new DateTime(entry.ModifiedTicks, DateTimeKind.Utc));
        entry.Restored = true; Save(manifest, entry);
        return entry.OriginalPath;
    }
    private static void Save(string path, QuarantineEntry entry)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
