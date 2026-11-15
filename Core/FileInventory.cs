using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace SimilarVideoFinder.Core;

public static class FileInventory
{
    public static List<FileRecord> Enumerate(ScanOptions options, CacheStore cache, Action<ScanProgress> progress,
        Action<string> error, CancellationToken cancellation, List<FileRecord>? inventory = null)
    {
        var extensions = options.Extensions.Split([' ', ',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(e => "." + e.Trim().TrimStart('.', '*')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = inventory ?? [];
        options.ExcludedFolders = options.ExcludedFolders.Select(CanonicalRoot).ToList();
        var referenceRoots = options.ReferenceRoots.Select(CanonicalRoot).ToArray();
        var pending = new Stack<string>();
        foreach (var root in options.Roots) pending.Push(CanonicalRoot(root));
        foreach (var root in referenceRoots) pending.Push(root);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batch = new List<FileRecord>(256);
        try
        {
            while (pending.TryPop(out var directory))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!visited.Add(directory.TrimEnd(System.IO.Path.DirectorySeparatorChar))) continue;
                if (ExclusionRules.Excluded(directory, options)) continue;
                try
                {
                    var info = new DirectoryInfo(directory);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { error($"Skipped junction/link: {directory}"); continue; }
                    foreach (var entry in info.EnumerateFileSystemInfos())
                    {
                        cancellation.ThrowIfCancellationRequested();
                        try
                        {
                            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            if (entry is DirectoryInfo sub) { if (options.Recursive) pending.Push(sub.FullName); continue; }
                            if (entry is not FileInfo file || !extensions.Contains(file.Extension) || !seenFiles.Add(file.FullName)) continue;
                            if (file.Length < options.MinimumSizeBytes || ExclusionRules.Excluded(file.FullName, options)) continue;
                            var record = options.ForceRescan ? null : cache.Load(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);
                            record ??= new() { Path = file.FullName, Size = file.Length, ModifiedTicks = file.LastWriteTimeUtc.Ticks };
                            record.IsReference = referenceRoots.Any(root => ReferencePolicy.IsWithin(record.Path, root));
                            files.Add(record); batch.Add(record);
                            if (batch.Count == 256) { cache.SaveMany(batch); batch.Clear(); progress(new("Discovering videos", files.Count, 0, Detail: directory)); }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error($"{entry.FullName}: {ex.Message}"); }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error($"{directory}: {ex.Message}"); }
            }
        }
        finally { if (batch.Count > 0) cache.SaveMany(batch); }
        progress(new("Inventory ready", files.Count, files.Count));
        return files;
    }
    public static string CanonicalRoot(string root)
    {
        string full = System.IO.Path.GetFullPath(root);
        if (!OperatingSystem.IsWindows()) return full;
        // Resolve mapped drive and UNC aliases once per root, rather than opening every file.
        using var handle = CreateFileW(full, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) return full;
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) return full;
        string canonical = buffer.ToString();
        if (canonical.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + canonical[8..];
        return canonical.StartsWith(@"\\?\", StringComparison.Ordinal) ? canonical[4..] : canonical;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
