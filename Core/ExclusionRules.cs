using System.Text.RegularExpressions;
namespace SimilarVideoFinder.Core;

public static class ExclusionRules
{
    public static bool Excluded(string path, ScanOptions options) => options.ExcludedFolders.Any(root => ReferencePolicy.IsWithin(path, root)) ||
        options.ExcludedPatterns.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(pattern => Regex.IsMatch(System.IO.Path.GetFileName(path), "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
}
