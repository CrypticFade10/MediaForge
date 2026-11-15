using SimilarVideoFinder.Core;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.ComponentModel;
using System.Windows.Media;
namespace SimilarVideoFinder.App;
public partial class MainWindow
{
    private async Task SmokeAsync()
    {
        string output = Arg("--output-dir") ?? App.DataDirectory;
        Directory.CreateDirectory(output);
        void SaveThemes(Window window, string name)
        {
            ThemeManager.Apply(false); DarkModeBox.IsChecked = false; window.UpdateLayout(); SaveImage(window, Path.Combine(output, name + ".png"));
            ThemeManager.Apply(true); DarkModeBox.IsChecked = true; window.UpdateLayout(); SaveImage(window, Path.Combine(output, name + "-dark.png"));
            ThemeManager.Apply(App.DarkMode); DarkModeBox.IsChecked = App.DarkMode;
        }
        SaveThemes(this, "app-ready");
        var settings = new ReviewSettingsWindow(this, Options(false), keepPreferences, (_, _) => { }) { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
        settings.Show(); SaveThemes(settings, "app-v3-preferences"); settings.Close();
        var discardDialog = new ConfirmationWindow(this, [new() { Path = @"C:\Disposable\all-copies.mkv", Size = 100 }], false, discardFiles: 1)
        { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
        discardDialog.Show();
        var discardPanels = ((Grid)discardDialog.Content).Children.OfType<StackPanel>();
        var discardEntry = discardPanels.SelectMany(p => p.Children.OfType<TextBox>()).Single();
        var discardConfirm = discardPanels.SelectMany(p => p.Children.OfType<Button>()).Single(b => !b.IsCancel);
        bool wholeGroupReview = !discardConfirm.IsEnabled;
        discardEntry.Text = "DELETE"; wholeGroupReview &= discardConfirm.IsEnabled;
        SaveThemes(discardDialog, "app-v3-discard"); discardDialog.Close();
        bool historyDefaultOff = !App.SaveHistory && cache?.IsPersistent == false && !File.Exists(Path.Combine(App.DataDirectory, "library.sqlite"));
        var networkDialog = new ConfirmationWindow(this, [new() { Path = @"\\server\share\network-fixture.mkv", Size = 100 }], false, 1)
        { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        networkDialog.Show();
        var confirmationPanels = ((Grid)networkDialog.Content).Children.OfType<StackPanel>();
        var networkEntry = confirmationPanels.SelectMany(p => p.Children.OfType<TextBox>()).Single();
        var networkConfirm = confirmationPanels.SelectMany(p => p.Children.OfType<Button>()).Single(b => b.Content as string == "Delete permanently");
        bool networkReview = !networkConfirm.IsEnabled;
        SaveThemes(networkDialog, "app-network-deletion");
        networkEntry.Text = "DELETE"; networkReview &= networkConfirm.IsEnabled; networkDialog.Close();
        string? library = Arg("--smoke-library");
        if (library != null)
        {
            var options = new ScanOptions { Roots = [library], VideoContent = true };
            ApplyOptions(options); report = await Task.Run(() => new ScanEngine(cache!, media!).ScanAsync(options, null, CancellationToken.None));
            ShowReport(report); SetBusy(false); await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            SaveThemes(this, "app-results");
            ReportExporter.WriteCsv(Path.Combine(output, "smoke-results.csv"), report);
            var group = report.Groups.FirstOrDefault(g => g.Kind == MatchKind.VideoContent);
            if (group != null)
            {
                var dialog = new EvidenceWindow(this, new ResultRow { Group = group, Member = group.Members[1] }, media!, cache!, report);
                dialog.ShowInTaskbar = false; dialog.WindowStartupLocation = WindowStartupLocation.Manual; dialog.Left = -10000; dialog.Top = -10000;
                dialog.Show(); await dialog.LoadFrames(0); await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                SaveThemes(dialog, "app-evidence");
                var selectors = ((Grid)dialog.Content).Children.OfType<StackPanel>().First(p => p.Children.OfType<ComboBox>().Count() == 2).Children.OfType<ComboBox>().ToArray();
                if (selectors.Length != 2 || selectors.Any(c => c.SelectedItem is not string)) throw new InvalidOperationException("Comparison selectors must display file paths.");
                selectors[0].SelectedIndex = 1; selectors[1].SelectedIndex = 0;
                await dialog.LoadFrames(-1, .5); SaveThemes(dialog, "app-evidence-timeline"); dialog.Close();
            }
            File.WriteAllText(Path.Combine(output, "smoke-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            var confirmationFiles = report.Groups.SelectMany(g => g.Members).Select(m => m.File).DistinctBy(f => f.Path).Take(2).ToArray();
            if (confirmationFiles.Length > 0)
            {
                foreach (bool permanent in new[] { false, true })
                {
                    var confirmation = new ConfirmationWindow(this, confirmationFiles, permanent) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
                    confirmation.Show(); await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                    SaveThemes(confirmation, permanent ? "app-permanent-confirmation" : "app-recycle-confirmation"); confirmation.Close();
                }
            }
        }
        bool initialSelectionEmpty = selectedPaths.Count == 0 && rows.All(r => !r.Selected);
        bool selectionSync = true;
        var repeated = rowsByPath.Values.FirstOrDefault(r => r.Count > 1);
        if (repeated != null)
        {
            repeated[0].Selected = true; selectionSync = repeated.All(r => r.Selected) && selectedPaths.Count == 1;
            ClearSelection_Click(this, new());
        }
        double largeLoadSeconds = 0, bulkSelectionSeconds = 0;
        bool bulkPreservesCopies = true, bulkRespectsFilter = true, groupSelectionSummaries = true;
        bool numericSorting = false, referenceProtection = false, deletionRefresh = false;
        var protectedFile = new FileRecord { Path = @"C:\ReferenceSmoke\protected.mkv", Size = 100, IsReference = true };
        var otherFile = new FileRecord { Path = @"C:\CandidateSmoke\other.mkv", Size = 200 };
        var referenceReport = new ScanReport { ReferenceRoots = [@"C:\ReferenceSmoke"], Groups = [new() { Id = "reference-smoke", Kind = MatchKind.IdenticalContents,
            Members = [new(protectedFile, new(100, "SHA-256")), new(otherFile, new(100, "SHA-256"))] }] };
        ShowReport(referenceReport); BulkSelect_Click(this, new());
        referenceProtection = selectedPaths.SetEquals([otherFile.Path]) && rows.Single(r => r.Path == protectedFile.Path).CanSelect == false;
        SaveThemes(this, "app-reference-protection");
        referenceFolders.Clear();
        var thirdFile = new FileRecord { Path = @"C:\CandidateSmoke\third.mkv", Size = 20 };
        var fourthFile = new FileRecord { Path = @"C:\CandidateSmoke\fourth.mkv", Size = 100 };
        var cleanup = new ScanReport { Groups = [new() { Id = "cleanup", Kind = MatchKind.IdenticalContents,
            Members = [new(otherFile, new(100, "SHA-256")), new(thirdFile, new(100, "SHA-256")), new(fourthFile, new(100, "SHA-256"))] }] };
        ShowReport(cleanup);
        RemoveDeleted([new(otherFile.Path, DeleteState.Recycled, "Synthetic outcome; no file was deleted")]); ShowReport(cleanup);
        deletionRefresh = rows.Count == 2 && report!.Groups.Count == 1 && rows.All(r => r.Path != otherFile.Path);
        Results_Sorting(this, new DataGridSortingEventArgs(ResultsGrid.Columns.First(c => c.SortMemberPath == nameof(ResultRow.SizeBytes))));
        numericSorting = view!.Cast<ResultRow>().First(r => !r.IsGroupHeader).Member.File.Size == 20;
        if (int.TryParse(Arg("--smoke-count"), out int count) && count > 0)
        {
            var synthetic = new ScanReport { ScanId = 999, FileCount = count };
            for (int i = 0; i < count / 2; i++) synthetic.Groups.Add(new() { Id = $"999-{i + 1:D5}", Kind = MatchKind.SimilarFilename,
                Members = [new(new() { Path = $@"C:\Synthetic\Show {i} S01E01 [h264].mkv", Size = 1500000000 }, new(100, "Synthetic reference")),
                    new(new() { Path = $@"C:\Synthetic\Show {i} S01E01 [h265].mkv", Size = 700000000 }, new(100, "Synthetic candidate"))] });
            var clock = Stopwatch.StartNew(); ShowReport(synthetic); await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            UpdateLayout(); clock.Stop(); largeLoadSeconds = clock.Elapsed.TotalSeconds;
            SaveThemes(this, "app-large-results");
            clock.Restart(); BulkSelect_Click(this, new()); clock.Stop(); bulkSelectionSeconds = clock.Elapsed.TotalSeconds;
            bulkPreservesCopies = selectedPaths.Count == count / 2 && DeletionService.ValidateSelection(synthetic, selectedPaths) == null
                && synthetic.Groups.All(g => !selectedPaths.Contains(g.Members[0].File.Path) && selectedPaths.Contains(g.Members[1].File.Path));
            groupSelectionSummaries = groupHeaders.Values.All(h => h.HeaderSelection.Contains("1 selected", StringComparison.Ordinal));
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            SaveThemes(this, "app-bulk-selection");
            TextFilter.Text = "Show 12 S01E01";
            BulkSelect_Click(this, new());
            bulkRespectsFilter = selectedPaths.Count == 1 && selectedPaths.All(p => p.Contains("Show 12 S01E01"));
            ClearSelection_Click(this, new()); TextFilter.Clear(); filterTimer.Stop(); RefreshFilter();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle); UpdateLayout();
        }
        int CountRows(DependencyObject visual)
        {
            int count = visual is DataGridRow ? 1 : 0;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(visual); i++) count += CountRows(VisualTreeHelper.GetChild(visual, i));
            return count;
        }
        int rendered = CountRows(ResultsGrid);
        bool groupedCollapse = true;
        if (grouped && view != null)
        {
            var header = view.Cast<ResultRow>().FirstOrDefault(r => r.IsGroupHeader);
            if (header != null)
            {
                collapsedGroups.Add(header.GroupId); view.Refresh();
                groupedCollapse = !view.Cast<ResultRow>().Any(r => !r.IsGroupHeader && r.GroupId == header.GroupId);
                collapsedGroups.Remove(header.GroupId); view.Refresh();
            }
        }
        await ChangeHistoryAsync(true);
        bool historyOptIn = App.SaveHistory && cache!.IsPersistent && cache.LoadSettings()?.Roots.SequenceEqual(folders) == true;
        DarkModeBox.IsChecked = true; DarkMode_Click(this, new());
        bool darkPreference = App.DarkMode && File.ReadAllText(Path.Combine(App.DataDirectory, "preferences.json")).Contains("\"DarkMode\":true");
        DarkModeBox.IsChecked = false; DarkMode_Click(this, new());
        await ChangeHistoryAsync(false);
        bool historyOptOut = !App.SaveHistory && !cache!.IsPersistent && cache.LoadSettings() == null;
        var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m => m.ModuleName.Contains("coreclr", StringComparison.OrdinalIgnoreCase) || m.ModuleName.Contains("sqlite", StringComparison.OrdinalIgnoreCase))
            .Select(m => new { m.ModuleName, m.FileName }).ToArray();
#pragma warning disable IL3000 // Deliberately verify the core assembly is embedded in the single-file smoke test.
        string coreLibraryLocation = typeof(object).Assembly.Location;
#pragma warning restore IL3000
        bool success = wholeGroupReview && initialSelectionEmpty && selectionSync && bulkPreservesCopies && bulkRespectsFilter && numericSorting && referenceProtection && deletionRefresh && historyDefaultOff && rendered < 100 && groupedCollapse && groupSelectionSummaries && historyOptIn && historyOptOut && darkPreference && networkReview && largeLoadSeconds < 5 && bulkSelectionSeconds < 5;
        File.WriteAllText(Path.Combine(output, "smoke.json"), JsonSerializer.Serialize(new { Success = success, ToolPath = media!.FFmpegPath,
            Modules = modules, Groups = report?.Groups.Count ?? 0, Rows = rows.Count, RenderedRows = rendered, Virtualisation = ResultsGrid.EnableRowVirtualization,
            InitialSelectionEmpty = initialSelectionEmpty, SelectionSynchronisesAcrossSearches = selectionSync, LargeResultLoadSeconds = largeLoadSeconds,
            BulkSelectionPreservesCopies = bulkPreservesCopies, BulkSelectionRespectsFilter = bulkRespectsFilter, BulkSelectionSeconds = bulkSelectionSeconds,
            HistoryDefaultsOff = historyDefaultOff, NumericSorting = numericSorting, ReferenceProtection = referenceProtection, DeletionRefresh = deletionRefresh, Grouped = GroupResultsBox.IsChecked == true,
            GroupCollapse = groupedCollapse, HistoryOptIn = historyOptIn, HistoryOptOut = historyOptOut, DarkPreference = darkPreference,
            GroupSelectionSummaries = groupSelectionSummaries,
            NetworkDeletionReview = networkReview, WholeGroupTypedReview = wholeGroupReview,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            CoreLibraryLocation = coreLibraryLocation,
            InstalledDotNetModules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m => m.FileName.Contains(@"\dotnet\shared\", StringComparison.OrdinalIgnoreCase)).Select(m => m.FileName).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
        Application.Current.Shutdown(success ? 0 : 1);
    }
}
