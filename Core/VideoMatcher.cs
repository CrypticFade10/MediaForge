namespace SimilarVideoFinder.Core;

public static class VideoMatcher
{
    public static bool CompatibleDuration(double a, double b) => Math.Abs(a - b) <= Math.Max(2, Math.Min(a, b) * 0.01);
    public static MatchEvidence? Compare(FileRecord a, FileRecord b, int maxDistance)
    {
        if (a.Media == null || b.Media == null || a.Frames?.Count != 12 || b.Frames?.Count != 12 ||
            !CompatibleDuration(a.Media.Duration, b.Media.Duration)) return null;
        var distances = new List<int>(12);
        int matches = 0;
        for (int i = 0; i < 12; i++)
        {
            int d = a.Frames[i].Informative && b.Frames[i].Informative ? PerceptualHash.Distance(a.Frames[i].Hash, b.Frames[i].Hash) : 64;
            distances.Add(d); if (d <= maxDistance) matches++;
        }
        if (matches < 9) return null;
        return new(100d * distances.Where(d => d <= maxDistance).Sum(d => 1 - d / 64d) / 12,
            $"{matches}/12 informative frames agree; duration difference {Math.Abs(a.Media.Duration - b.Media.Duration):0.##}s", distances);
    }
    public static List<MatchGroup> Find(IReadOnlyList<FileRecord> files, int maxDistance, CancellationToken cancellation = default)
    {
        // Four anchors guarantee one agreement when at least nine of twelve frames agree.
        // BK trees return all hashes within the threshold; no lossy prefix hash filtering.
        int[] anchors = [1, 4, 7, 10];
        var durationIndex = new Dictionary<long, BkTree[]>();
        var indexed = new List<FileRecord>();
        var builder = new DirectGroupBuilder(MatchKind.VideoContent);
        foreach (var file in files.OrderBy(f => f.Media?.Duration ?? double.MaxValue))
        {
            cancellation.ThrowIfCancellationRequested();
            if (file.Media == null || file.Frames?.Count != 12 || file.Frames.Count(f => f.Informative) < 9) continue;
            var ids = new HashSet<int>();
            double tolerance = Math.Max(2, file.Media.Duration * 0.01);
            long firstBucket = (long)Math.Floor((file.Media.Duration - tolerance) / 5);
            long lastBucket = (long)Math.Floor((file.Media.Duration + tolerance) / 5);
            for (long bucket = firstBucket; bucket <= lastBucket; bucket++)
                if (durationIndex.TryGetValue(bucket, out var trees))
                    for (int i = 0; i < anchors.Length; i++) if (file.Frames[anchors[i]].Informative)
                        trees[i].Find(file.Frames[anchors[i]].Hash, maxDistance, ids, cancellation);
            foreach (int id in ids.Order())
            {
                cancellation.ThrowIfCancellationRequested();
                if (Compare(indexed[id], file, maxDistance) is { } evidence) builder.Add(indexed[id], file, evidence);
            }
            int next = indexed.Count; indexed.Add(file);
            long ownBucket = (long)Math.Floor(file.Media.Duration / 5);
            if (!durationIndex.TryGetValue(ownBucket, out var ownTrees)) durationIndex[ownBucket] = ownTrees = anchors.Select(_ => new BkTree()).ToArray();
            for (int i = 0; i < anchors.Length; i++) if (file.Frames[anchors[i]].Informative) ownTrees[i].Add(file.Frames[anchors[i]].Hash, next);
        }
        return builder.Groups;
    }
    private sealed class BkTree
    {
        private sealed class Node(ulong hash, int id)
        {
            public ulong Hash = hash;
            public List<int> Ids = [id];
            public Dictionary<int, Node> Children = [];
        }
        private Node? root;
        public void Add(ulong hash, int id)
        {
            if (root == null) { root = new(hash, id); return; }
            var node = root;
            while (true)
            {
                int d = PerceptualHash.Distance(node.Hash, hash);
                if (d == 0) { node.Ids.Add(id); return; }
                if (!node.Children.TryGetValue(d, out var child)) { node.Children[d] = new(hash, id); return; }
                node = child;
            }
        }
        public void Find(ulong hash, int radius, HashSet<int> matches, CancellationToken cancellation)
        {
            if (root == null) return;
            var stack = new Stack<Node>(); stack.Push(root);
            while (stack.TryPop(out var node))
            {
                cancellation.ThrowIfCancellationRequested();
                int d = PerceptualHash.Distance(node.Hash, hash);
                if (d <= radius) foreach (int id in node.Ids) matches.Add(id);
                foreach (var child in node.Children) if (child.Key >= d - radius && child.Key <= d + radius) stack.Push(child.Value);
            }
        }
    }
}
