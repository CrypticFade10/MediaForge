using System.Text;
using System.Text.RegularExpressions;

namespace SimilarVideoFinder.Core;

public sealed record ParsedName(string Text, string Title, string? Year, string? Episode, string[] Tokens, string Edition = "");

public static class FilenameMatcher
{
    private static readonly Regex Episodes = new(@"(?<![a-z0-9])(?:s(?<s>\d{1,3})[ ._-]*e(?<e>\d{1,3})(?<more>(?:[ ._-]*e\d{1,3})*)|(?<s>\d{1,3})x(?<e>\d{1,3})(?<more>(?:[ ._-]*x\d{1,3})*))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Technical = new(@"\b(?:sdtv|hdtv|pdtv|dsr|dvdrip|dvd|bluray|blu[ ._-]?ray|bdrip|brrip|web[ ._-]?dl|webrip|remux|xvid|divx|[xh][ ._-]?26[45]|hevc|avc|av1|vp9|aac|mp3|ac3|eac3|ddp|dts(?:[ ._-]?hd)?|truehd|flac|opus|pcm|10bit|8bit|hdr10|hdr|dolby[ ._-]?vision|2160p|1080[pi]|720p|576[pi]|480[pi]|4k|uhd)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Year = new(@"\b(?:19|20)\d{2}\b", RegexOptions.Compiled);
    private static readonly Regex Words = new(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);
    private static readonly Regex Editions = new(@"\b(?:director['']?s[ ._-]+cut|extended(?:[ ._-]+cut)?|theatrical(?:[ ._-]+cut)?|unrated|uncut|final[ ._-]+cut|special[ ._-]+edition|remastered)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ParsedName Parse(string filename)
    {
        var text = System.IO.Path.GetFileNameWithoutExtension(filename).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        text = Regex.Replace(text, @"\btt\d{5,}\b", " ");
        if (Technical.IsMatch(text))
        {
            text = Regex.Replace(text, @"-\s*[a-z0-9]+$", " ");
            text = Technical.Replace(text, " ");
            text = Regex.Replace(text, @"\b(?:proper|repack)\b", " ");
            text = Regex.Replace(text, @"\b(?:2[ .]0|5[ .]1|7[ .]1)\b", " ");
        }
        string edition = string.Join('|', Editions.Matches(text).Select(m => Normalise(m.Value.Replace("'", "").Replace("'", ""))).Distinct().Order(StringComparer.Ordinal));
        string? episode = null;
        var episodeMatch = Episodes.Match(text);
        var title = episodeMatch.Success ? text[..episodeMatch.Index] : text;
        if (episodeMatch.Success)
        {
            var season = int.Parse(episodeMatch.Groups["s"].Value);
            var first = int.Parse(episodeMatch.Groups["e"].Value);
            var numbers = new SortedSet<int> { first };
            var more = episodeMatch.Groups["more"].Value;
            var extras = Regex.Matches(more, @"[ex](\d+)", RegexOptions.IgnoreCase)
                .Select(m => int.Parse(m.Groups[1].Value)).ToArray();
            if (more.TrimStart().StartsWith('-') && extras.Length == 1 && extras[0] >= first && extras[0] - first < 100)
                for (int e = first; e <= extras[0]; e++) numbers.Add(e);
            else foreach (var e in extras) numbers.Add(e);
            episode = $"{season}:{string.Join(',', numbers)}";
            text = Episodes.Replace(text, " ");
        }
        var year = Year.Match(title) is { Success: true } match ? match.Value : null;
        title = Normalise(Year.Replace(title, " "));
        text = Normalise(text);
        return new(text, title, year, episode, Words.Matches(episode == null ? text : title).Select(m => m.Value).Distinct().ToArray(), edition);
    }
    private static string Normalise(string text) => string.Join(' ', Words.Matches(text).Select(m => m.Value));
    public static MatchEvidence? Compare(ParsedName a, ParsedName b, int threshold)
    {
        if (a.Year != null && b.Year != null && a.Year != b.Year) return null;
        if (a.Edition != b.Edition) return null;
        if (a.Episode != b.Episode) return null;
        if (a.Tokens.Length == 0 || b.Tokens.Length == 0) return null;
        double score = 200d * a.Tokens.Intersect(b.Tokens, StringComparer.Ordinal).Count() / (a.Tokens.Length + b.Tokens.Length);
        if (score + 0.00001 < threshold) return null;
        return new(score, a.Episode is { } e ? $"Same season/episode ({e}); title similarity {score:0.#}%" : $"Normalised title token similarity {score:0.#}%");
    }

    public static List<MatchGroup> Find(IReadOnlyList<FileRecord> files, int threshold, CancellationToken cancellation = default)
    {
        // Inverted token indexes restrict comparisons to plausible existing representatives.
        var prepared = files.Select(file => (File: file, Name: Parse(file.Name))).ToArray();
        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in prepared) foreach (var token in item.Name.Tokens)
            frequencies[token] = frequencies.GetValueOrDefault(token) + 1;
        double jaccard = threshold / (200d - threshold);
        string[] Prefix(ParsedName name) => name.Tokens.OrderBy(t => frequencies[t]).ThenBy(t => t, StringComparer.Ordinal)
            .Take(name.Tokens.Length - (int)Math.Ceiling(jaccard * name.Tokens.Length - 1e-9) + 1).ToArray();
        var builder = new DirectGroupBuilder(MatchKind.SimilarFilename);
        var indexed = new List<(FileRecord File, ParsedName Name)>();
        var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var item in prepared)
        {
            cancellation.ThrowIfCancellationRequested();
            if (item.Name.Tokens.Length == 0) continue;
            var ids = new HashSet<int>();
            foreach (var token in Prefix(item.Name))
                if (index.TryGetValue((item.Name.Episode ?? "film") + "|" + token, out var matches)) ids.UnionWith(matches);
            foreach (int id in ids.Order())
            {
                cancellation.ThrowIfCancellationRequested();
                var prior = indexed[id];
                if (Compare(prior.Name, item.Name, threshold) is { } evidence) builder.Add(prior.File, item.File, evidence);
            }
            int next = indexed.Count; indexed.Add(item);
            foreach (var token in Prefix(item.Name))
            {
                var key = (item.Name.Episode ?? "film") + "|" + token;
                if (!index.TryGetValue(key, out var matches)) index[key] = matches = [];
                matches.Add(next);
            }
        }
        return builder.Groups;
    }
}