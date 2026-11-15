using System.Text.Json;

namespace SimilarVideoFinder.Core;

public enum DeleteState { Recycled, DeletedPermanently, RequiresPermanentConfirmation, Skipped, Failed, Quarantined }
public sealed record DeleteOutcome(string Path, DeleteState State, string Message, string? AuditWarning = null);
public interface IDeletionBackend
{
    DeleteOutcome TryRecycle(FileRecord file);
    DeleteOutcome DeletePermanently(FileRecord file);
}

public sealed class DeletionService(IDeletionBackend backend, Func<string, bool>? networkPath = null)
{
    public static string? ValidateSelection(ScanReport report, IReadOnlySet<string> selected, IReadOnlySet<string>? discardPaths = null)
    {
        if (selected.Count == 0) return "Select files using the deletion checkboxes first.";
        var selection = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        var reportPaths = report.Groups.SelectMany(g => g.Members).Select(m => m.File.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selection.Any(p => !reportPaths.Contains(p)))
            return "The selection contains files outside this report.";
        var eligible = ReferencePolicy.EligiblePaths(report);
        if (selection.Any(p => !eligible.Contains(p))) return "Reference files are protected. Only files with a direct reference match can be deleted when reference folders are configured.";
        var discarded = new HashSet<string>(discardPaths ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var group in report.Groups)
            foreach (var member in group.Members.Where(m => selection.Contains(m.File.Path) && !discarded.Contains(m.File.Path)))
                if (!group.Members.Any(m => !selection.Contains(m.File.Path) && ReferencePolicy.Compare(group, member.File, m.File, report) != null))
                    return $"Keep a directly matching copy for {member.File.Name} in group {group.Id}, or explicitly choose Delete whole group. A file selected in another search is selected everywhere.";
        return null;
    }
    public List<DeleteOutcome> Execute(ScanReport report, IReadOnlySet<string> selected, bool permanentConfirmed,
        string? auditPath = null, CancellationToken cancellation = default, Action<DeleteOutcome>? auditWriter = null, bool networkPermanentConfirmed = false,
        IReadOnlySet<string>? discardPaths = null, Action<int, int>? progress = null)
    {
        selected = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        var discarded = new HashSet<string>(discardPaths ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
        var invalid = ValidateSelection(report, selected, discarded);
        if (invalid != null) throw new InvalidOperationException(invalid);
        var outcomes = new List<DeleteOutcome>();
        // Open the audit before any media operation. Keep completed outcomes even if later writes fail.
        StreamWriter? audit = null;
        if (auditPath != null)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(auditPath))!);
            audit = new StreamWriter(new FileStream(auditPath, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        }
        using var auditLifetime = new SafeAuditLifetime(audit);
        string? auditFailure = null;
        bool referencesRequired = report.ReferenceRoots.Count > 0 || report.Groups.Any(g => g.Members.Any(m => m.File.IsReference));
        var files = report.Groups.SelectMany(g => g.Members).Select(m => m.File)
            .Where(f => selected.Contains(f.Path)).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var membership = new Dictionary<string, List<MatchGroup>>(StringComparer.OrdinalIgnoreCase);
        var retained = new Dictionary<MatchGroup, FileRecord[]>();
        foreach (var group in report.Groups)
        {
            retained[group] = group.Members.Where(m => !selected.Contains(m.File.Path)).Select(m => m.File).ToArray();
            foreach (var member in group.Members)
            {
                if (!membership.TryGetValue(member.File.Path, out var groups)) membership[member.File.Path] = groups = [];
                groups.Add(group);
            }
        }
        foreach (var file in files)
        {
            if (cancellation.IsCancellationRequested) { outcomes.Add(new(file.Path, DeleteState.Skipped, "Deletion cancelled before this file.")); continue; }
            var affected = membership[file.Path];
            DeleteOutcome outcome;
            if (ReferencePolicy.Protected(report, file)) outcome = new(file.Path, DeleteState.Skipped, "Protected reference file.");
            else if (!file.IsCurrent()) outcome = new(file.Path, DeleteState.Skipped, "File changed or is unavailable; rescan before deleting.");
            else if (referencesRequired && !ReferencePolicy.HasCurrentReference(report, file, affected)) outcome = new(file.Path, DeleteState.Skipped, "A matching protected reference changed or is unavailable; rescan before deleting.");
            else if (!discarded.Contains(file.Path) && affected.Any(g => !retained[g].Any(f => ReferencePolicy.Compare(g, file, f, report) != null && f.IsCurrent())))
                outcome = new(file.Path, DeleteState.Skipped, "A retained copy changed or is unavailable; rescan before deleting.");
            else
            {
                try
                {
                    bool network = (networkPath ?? DeletionPolicy.IsNetworkPath)(file.Path);
                    if (network)
                        outcome = permanentConfirmed || networkPermanentConfirmed ? backend.DeletePermanently(file)
                            : new(file.Path, DeleteState.RequiresPermanentConfirmation, "Network files are deleted permanently after explicit confirmation; recycling is not attempted.");
                    else outcome = backend.TryRecycle(file);
                    if (!network && outcome.State == DeleteState.RequiresPermanentConfirmation && permanentConfirmed)
                    {
                        // A second validation closes the interval spent in shell callbacks or confirmation dialogs.
                        if (!file.IsCurrent() || referencesRequired && !ReferencePolicy.HasCurrentReference(report, file, affected) || !discarded.Contains(file.Path) && affected.Any(g => !retained[g].Any(f => ReferencePolicy.Compare(g, file, f, report) != null && f.IsCurrent())))
                            outcome = new(file.Path, DeleteState.Skipped, "Files changed before permanent deletion.");
                        else outcome = backend.DeletePermanently(file);
                    }
                }
                catch (Exception ex) { outcome = new(file.Path, DeleteState.Failed, ex.Message); }
            }
            if ((audit != null || auditWriter != null) && auditFailure == null)
            {
                try { auditWriter?.Invoke(outcome); audit?.WriteLine(JsonSerializer.Serialize(new { Time = DateTime.UtcNow, Outcome = outcome })); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { auditFailure = "Audit write failed: " + ex.Message; }
            }
            outcomes.Add(outcome with { AuditWarning = auditFailure });
            progress?.Invoke(outcomes.Count, files.Length);
        }
        return outcomes;
    }
    private sealed class SafeAuditLifetime(StreamWriter? writer) : IDisposable
    {
        public void Dispose() { try { writer?.Dispose(); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
