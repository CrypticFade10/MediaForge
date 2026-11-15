namespace SimilarVideoFinder.Core;

public static class DeletionPolicy
{
    public static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try
        {
            string? root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
            return OperatingSystem.IsWindows() && root != null && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
