using SimilarVideoFinder.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace SimilarVideoFinder.App;
public partial class MainWindow
{
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        bool quarantine = quarantineRequested; quarantineRequested = false;
        ResultsGrid.CommitEdit(DataGridEditingUnit.Cell, true); ResultsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (report == null || cache == null || busy) return;
        var selection = new HashSet<string>(selectedPaths, StringComparer.OrdinalIgnoreCase);
        var discarded = new HashSet<string>(discardPaths.Where(selection.Contains), StringComparer.OrdinalIgnoreCase);
        string? invalid = DeletionService.ValidateSelection(report, selection, discarded);
        if (invalid != null) { MessageBox.Show(this, invalid, "Review selection"); return; }
        var files = selection.Select(p => rowsByPath[p][0].Member.File).ToArray();
        int networkCount = quarantine ? 0 : files.Count(f => DeletionPolicy.IsNetworkPath(f.Path));
        SetBusy(true); cancellation = new();
        try
        {
            StatusText.Text = "Checking metadata and retained copies for review"; CancelButton.Content = "Cancel operation"; CancelButton.IsEnabled = true;
            var token = cancellation.Token;
            var reviewFiles = files.Concat(files.SelectMany(f => ReviewAnalysis.Retained(report, f, selection))).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            if (media != null) await Task.Run(async () =>
            {
                foreach (var file in reviewFiles)
                {
                    token.ThrowIfCancellationRequested();
                    if (file.Media != null || !file.IsCurrent()) continue;
                    try { file.Media = await media.ProbeAsync(file.Path, token); cache.Save(file); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException) { }
                }
            }, token);
            token.ThrowIfCancellationRequested();
            string Details(FileRecord file)
            {
                var retained = ReviewAnalysis.Retained(report, file, selection);
                return (discarded.Contains(file.Path) ? "WHOLE GROUP DISCARD: no retained copy required.\n" : "Retained direct matches:\n" + string.Join("\n", retained.Select(f => f.Path)) + "\n") +
                    string.Join("\n", ReviewAnalysis.LossWarnings(file, retained));
            }
            if (new ConfirmationWindow(this, files, false, networkCount, discarded.Count, files.ToDictionary(f => f.Path, Details, StringComparer.OrdinalIgnoreCase), quarantine).ShowDialog() != true) return;
            token.ThrowIfCancellationRequested();
            IDeletionBackend backend = quarantine ? new QuarantineBackend(quarantineFolder) : new ShellDeletionBackend();
            var service = new DeletionService(backend, quarantine ? _ => false : null);
            string? auditPath = App.SaveHistory ? Path.Combine(App.DataDirectory, "deletions.jsonl") : null;
            var progress = new Progress<(int Completed, int Total)>(p => ShowProgress(new(quarantine ? "Quarantining files" : "Deleting files", p.Completed, p.Total)));
            void Update(int completed, int total) => ((IProgress<(int, int)>)progress).Report((completed, total));
            var results = await StaTask.Run(() => service.Execute(report, selection, false, auditPath, token, networkPermanentConfirmed: networkCount > 0, discardPaths: discarded, progress: Update));
            ApplyOutcomes(results);
            var permanent = results.Where(r => r.State == DeleteState.RequiresPermanentConfirmation).Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (permanent.Count > 0 && !token.IsCancellationRequested)
            {
                var remaining = permanent.Select(p => rowsByPath[p][0].Member.File).ToArray();
                if (new ConfirmationWindow(this, remaining, true, discardFiles: discarded.Count(p => permanent.Contains(p))).ShowDialog() == true)
                {
                    var second = await StaTask.Run(() => service.Execute(report, permanent, true, auditPath, token, discardPaths: discarded, progress: Update));
                    ApplyOutcomes(second); results.AddRange(second);
                }
            }
            report.DeletionOutcomes.AddRange(results);
            RemoveDeleted(results); ShowReport(report); cache.FinishScan(report);
            StatusText.Text = token.IsCancellationRequested ? "Operation cancelled; completed outcomes retained" : quarantine ? "Quarantine complete" : "Deletion review complete";
            DetailText.Text = "See Deletion outcomes for each file. Quarantine copies can be restored using their restore manifests.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Operation cancelled"; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "File operation error"); }
        finally { cancellation.Dispose(); cancellation = null; CancelButton.Content = "Cancel scan"; SetBusy(false); if (closeRequested) Close(); }
    }
}
