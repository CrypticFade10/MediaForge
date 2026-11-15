namespace SimilarVideoFinder.Core;

public static class ReportMaintenance
{
    public static void Prune(ScanReport report, Func<FileRecord, bool> keep, CancellationToken cancellation = default)
    {
        var rebuilt = new List<MatchGroup>();
        try {
        foreach (var group in report.Groups)
        {
            cancellation.ThrowIfCancellationRequested();
            var members = new List<MatchMember>();
            foreach (var member in group.Members) { cancellation.ThrowIfCancellationRequested(); if (keep(member.File)) members.Add(member); }
            if (members.Count < 2) continue;
            if (members[0].File == group.Representative || group.Kind is not (MatchKind.SimilarFilename or MatchKind.VideoContent))
            {
                group.Members = members; rebuilt.Add(group);
            }
            else
            {
                var files = members.Select(m => m.File).ToArray();
                var replacements = group.Kind == MatchKind.SimilarFilename ? FilenameMatcher.Find(files, report.NameThreshold, cancellation) : VideoMatcher.Find(files, report.FrameDistance, cancellation);
                for (int i = 0; i < replacements.Count; i++) { replacements[i].Id = i == 0 ? group.Id : group.Id + "." + (i + 1); rebuilt.Add(replacements[i]); }
            }
        }
        } finally { report.Groups = rebuilt; }
    }
}
