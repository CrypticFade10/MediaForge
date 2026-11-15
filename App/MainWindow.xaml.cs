using Microsoft.Win32;
using SimilarVideoFinder.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using System.Collections;

namespace SimilarVideoFinder.App;

public partial class MainWindow : Window
{
    public static RoutedUICommand ToggleGroupCommand { get; } = new("Toggle group", "ToggleGroup", typeof(MainWindow));
    private readonly string[] arguments;
    private readonly ObservableCollection<string> folders = [];
    private readonly ObservableCollection<string> referenceFolders = [];
    private readonly DispatcherTimer filterTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer settingsTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool applyingOptions;
    private string filterText = "";
    private int filterKind;
    private readonly HashSet<string> collapsedGroups = new(StringComparer.Ordinal);
    private HashSet<string> visibleGroups = new(StringComparer.Ordinal);
    private bool grouped;
    private readonly Dictionary<string, ResultRow> groupHeaders = new(StringComparer.Ordinal);
    private CacheStore? cache;
    private MediaProcessor? media;
    private ScanReport? report;
    private List<ResultRow> rows = [];
    private Dictionary<string, List<ResultRow>> rowsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private ICollectionView? view;
    private CancellationTokenSource? cancellation;
    private bool busy, syncingSelection, closeRequested;
    public MainWindow(string[] args)
    {
        arguments = args; InitializeComponent(); FolderList.ItemsSource = folders;
        CommandBindings.Add(new CommandBinding(DiscardGroupCommand, (_, e) => WholeGroup_Click(new Button { Tag = e.Parameter }, new RoutedEventArgs())));
        ReferenceFolderList.ItemsSource = referenceFolders;
        DarkModeBox.IsChecked = App.DarkMode; HistoryToggle.IsChecked = App.SaveHistory;
        HistoryToggle.Content = App.SaveHistory ? "Save history: ON" : "Save history: OFF";
        HistoryToggle.IsEnabled = false;
        ExtensionsBox.Text = new ScanOptions().Extensions;
        filterTimer.Tick += (_, _) => { filterTimer.Stop(); RefreshFilter(); };
        settingsTimer.Tick += (_, _) => { settingsTimer.Stop(); SaveCurrentSettings(); };
        folders.CollectionChanged += (_, _) => ScheduleSettingsSave();
        referenceFolders.CollectionChanged += (_, _) =>
        {
            ScheduleSettingsSave();
            if (!applyingOptions && report != null)
            {
                report.ReferenceRoots = report.ReferenceRoots.Concat(referenceFolders.Select(Path.GetFullPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                ShowReport(report);
                DetailText.Text = "Reference protection updated. Rescan to apply folder changes to the matching results.";
            }
        };
        SettingsPanel.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => ScheduleSettingsSave()));
        SettingsPanel.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => ScheduleSettingsSave()));
        SettingsPanel.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => ScheduleSettingsSave()));
        Loaded += LoadedAsync; Closing += ClosingWindow;
        Activated += (_, _) => DarkModeBox.IsChecked = App.DarkMode;
        if (args.Contains("--smoke") || args.Contains("--suite-smoke")) { ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.Manual; Left = -10000; Top = -10000; }
    }
    private async void LoadedAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            cache = await Task.Run(() => new CacheStore(Path.Combine(App.DataDirectory, "library.sqlite"), App.SaveHistory));
            media = await Task.Run(() => ToolBundle.Prepare(App.DataDirectory));
            ApplyOptions(cache.LoadSettings() ?? new());
            StatusText.Text = "Ready to scan"; ScanButton.IsEnabled = true;
            HistoryToggle.IsEnabled = true; HistoryButton.IsEnabled = App.SaveHistory;
            if (arguments.Contains("--smoke")) await SmokeAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Unable to prepare the app"; DetailText.Text = ex.Message;
            if (arguments.Contains("--smoke"))
            { File.WriteAllText(Path.Combine(App.DataDirectory, "smoke-error.txt"), ex.ToString()); Application.Current.Shutdown(1); }
        }
    }
    private void ApplyOptions(ScanOptions options)
    {
        applyingOptions = true;
        try {
        folders.Clear(); foreach (var root in options.Roots) folders.Add(root);
        referenceFolders.Clear(); foreach (var root in options.ReferenceRoots) referenceFolders.Add(root);
        RecursiveBox.IsChecked = options.Recursive; ExactNameBox.IsChecked = options.IdenticalFilename;
        HashBox.IsChecked = options.IdenticalContents; SimilarNameBox.IsChecked = options.SimilarFilename; VideoBox.IsChecked = options.VideoContent;
        EpisodeFolderBox.IsChecked = options.EpisodeInFolder;
        ExtensionsBox.Text = options.Extensions; NameThresholdBox.Text = options.NameThreshold.ToString();
        extraOptions = options; keepPreferences = options.KeepPreferences; FrameDistanceBox.Text = options.FrameDistance.ToString(); WorkersBox.Text = options.Workers.ToString(); ForceBox.IsChecked = false;
        } finally { applyingOptions = false; }
    }
    private void ScheduleSettingsSave()
    {
        if (!applyingOptions && App.SaveHistory && cache != null) { settingsTimer.Stop(); settingsTimer.Start(); }
    }
    private void SaveCurrentSettings()
    {
        if (!App.SaveHistory || cache == null || busy) return;
        try { cache.SaveSettings(Options(false)); }
        catch (ArgumentException) { } // An incomplete form is not a saved configuration.
        catch (Exception ex) { StatusText.Text = "Unable to save settings"; DetailText.Text = ex.Message; }
    }
    private void DarkMode_Click(object sender, RoutedEventArgs e)
    {
        try { App.SetPreferences(App.SaveHistory, DarkModeBox.IsChecked == true); }
        catch (Exception ex) { DarkModeBox.IsChecked = App.DarkMode; MessageBox.Show(this, ex.Message, "Theme preference"); }
    }
    private async void HistoryPreference_Click(object sender, RoutedEventArgs e)
    { await ChangeHistoryAsync(HistoryToggle.IsChecked == true); }
    private async Task ChangeHistoryAsync(bool enabled)
    {
        if (busy || cache == null) { HistoryToggle.IsChecked = App.SaveHistory; return; }
        CacheStore? replacement = null;
        SetBusy(true); settingsTimer.Stop();
        try
        {
            replacement = await Task.Run(() => new CacheStore(Path.Combine(App.DataDirectory, "library.sqlite"), enabled));
            App.SetPreferences(enabled, App.DarkMode);
            cache.Dispose(); cache = replacement; replacement = null;
            if (enabled)
            {
                cache.SaveSettings(Options(false));
                if (report != null) { report.ScanId = cache.BeginScan(Options(false)); cache.FinishScan(report); }
            }
            HistoryToggle.Content = enabled ? "Save history: ON" : "Save history: OFF";
            HistoryToggle.IsChecked = enabled;
            StatusText.Text = enabled ? "History saving enabled" : "History saving disabled";
            DetailText.Text = enabled ? "Folders, scan cache, reports, audit and error logs will be saved on this PC." : "Session-only storage. Previously saved history stays on disk but will not be loaded while history is off.";
        }
        catch (Exception ex) { replacement?.Dispose(); HistoryToggle.IsChecked = App.SaveHistory; MessageBox.Show(this, ex.Message, "History preference"); }
        finally { SetBusy(false); }
    }
    private ScanOptions Options(bool validate = true)
    {
        if (!int.TryParse(NameThresholdBox.Text, out int names) || !int.TryParse(FrameDistanceBox.Text, out int frames) || !int.TryParse(WorkersBox.Text, out int workers))
            throw new ArgumentException("Advanced settings must be whole numbers.");
        var options = new ScanOptions { Roots = folders.ToList(), ReferenceRoots = referenceFolders.ToList(), Recursive = RecursiveBox.IsChecked == true,
            IdenticalFilename = ExactNameBox.IsChecked == true, IdenticalContents = HashBox.IsChecked == true,
            SimilarFilename = SimilarNameBox.IsChecked == true, VideoContent = VideoBox.IsChecked == true,
            EpisodeInFolder = EpisodeFolderBox.IsChecked == true,
            Extensions = ExtensionsBox.Text, NameThreshold = names, FrameDistance = frames, Workers = workers, ForceRescan = ForceBox.IsChecked == true };
        options.ExcludedFolders = extraOptions.ExcludedFolders; options.ExcludedPatterns = extraOptions.ExcludedPatterns; options.MinimumSizeBytes = extraOptions.MinimumSizeBytes; options.MinimumDurationSeconds = extraOptions.MinimumDurationSeconds; options.KeepPreferences = keepPreferences; if (validate) options.Validate(); return options;
    }
    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose video folders", Multiselect = true };
        if (dialog.ShowDialog(this) == true) foreach (var path in dialog.FolderNames) AddPath(path);
    }
    private void AddPath(string path)
    {
        try
        {
            path = Path.GetFullPath(path.Trim().Trim('"'));
            if (!folders.Contains(path, StringComparer.OrdinalIgnoreCase)) folders.Add(path);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Folder path"); }
    }
    private void ManualPath_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(ManualPath.Text)) { AddPath(ManualPath.Text); ManualPath.Clear(); e.Handled = true; } }
    private void RemoveFolder_Click(object sender, RoutedEventArgs e) { if (FolderList.SelectedItem is string folder) folders.Remove(folder); }
    private void AddReference_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose protected reference folders", Multiselect = true };
        if (dialog.ShowDialog(this) == true) foreach (var path in dialog.FolderNames) AddReference(path);
    }
    private void AddReference(string path)
    {
        try { path = Path.GetFullPath(path.Trim().Trim('"')); if (!referenceFolders.Contains(path, StringComparer.OrdinalIgnoreCase)) referenceFolders.Add(path); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Reference folder path"); }
    }
    private void ReferencePath_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(ManualReferencePath.Text)) { AddReference(ManualReferencePath.Text); ManualReferencePath.Clear(); e.Handled = true; } }
    private void RemoveReference_Click(object sender, RoutedEventArgs e) { if (ReferenceFolderList.SelectedItem is string folder) referenceFolders.Remove(folder); }
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (busy || cache == null || media == null) return;
        ScanOptions options;
        try { options = Options(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Check scan settings"); return; }
        cancellation = new(); SetBusy(true); CancelButton.IsEnabled = true; selectedPaths.Clear(); UpdateSelection();
        try
        {
            var engine = new ScanEngine(cache, media); var progress = new Progress<ScanProgress>(ShowProgress);
            report = await Task.Run(() => engine.ScanAsync(options, progress, cancellation.Token));
            ShowReport(report); ForceBox.IsChecked = false;
        }
        catch (Exception ex) { StatusText.Text = "Scan failed"; DetailText.Text = ex.Message; MessageBox.Show(this, ex.Message, "Scan error"); }
        finally { cancellation.Dispose(); cancellation = null; SetBusy(false); if (closeRequested) Close(); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { cancellation?.Cancel(); CancelButton.IsEnabled = false; StatusText.Text = "Cancelling after the current file finishes."; }
    private void ShowProgress(ScanProgress p)
    {
        StatusText.Text = p.Stage; DetailText.Text = p.Detail;
        ScanProgressBar.IsIndeterminate = p.Total <= 0 && busy;
        if (p.Total > 0) { ScanProgressBar.Maximum = p.Total; ScanProgressBar.Value = p.Completed; }
        ProgressText.Text = $"{TimeSpan.FromSeconds(p.ElapsedSeconds):hh\\:mm\\:ss} | {p.CacheHits:N0} cache hits | " + (p.Total > 0 ? $"{p.Completed:N0} / {p.Total:N0}" : $"{p.Completed:N0} videos") + (p.BytesRead > 0 && p.ElapsedSeconds > 0 ? $" | hash I/O avg {Formatting.Size((long)(p.BytesRead / p.ElapsedSeconds))}/s" : "") + (p.RemainingSeconds is { } eta ? $" | stage ~{TimeSpan.FromSeconds(Math.Min(eta, 359999)):hh\\:mm\\:ss} left" : "");
    }
    private void SetBusy(bool value)
    {
        busy = value; SettingsPanel.IsEnabled = !value; ScanButton.IsEnabled = !value && media != null;
        HistoryButton.IsEnabled = !value && App.SaveHistory; HistoryToggle.IsEnabled = !value && cache != null; ResultsGrid.IsEnabled = !value; ReviewActions.IsEnabled = !value;
        BulkActions.IsEnabled = !value; BulkSelectButton.IsEnabled = !value && report?.Groups.Count > 0;
        ExportButton.IsEnabled = !value && report != null; DeleteButton.IsEnabled = !value && selectedPaths.Count > 0;
        if (!value) { CancelButton.IsEnabled = false; ScanProgressBar.IsIndeterminate = false; }
    }
    private void ShowReport(ScanReport next)
    {
        report = next; selectedPaths.Clear(); discardPaths.Clear();
        foreach (var row in rows) row.PropertyChanged -= RowChanged;
        next.ReferenceRoots = next.ReferenceRoots.Concat(referenceFolders.Select(Path.GetFullPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var file in next.Groups.SelectMany(g => g.Members).Select(m => m.File)) file.IsReference = ReferencePolicy.Protected(next, file);
        var eligible = ReferencePolicy.EligiblePaths(next);
        rows = next.Groups.SelectMany(g => g.Members.Select(m => new ResultRow { Group = g, Member = m, ReferenceEligible = eligible.Contains(m.File.Path) })).ToList();
        var outcomes = next.DeletionOutcomes.GroupBy(o => o.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows) if (outcomes.TryGetValue(row.Path, out var outcome)) row.State = outcome.State + ": " + outcome.Message;
        rowsByPath = rows.GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows) row.PropertyChanged += RowChanged;
        filterText = TextFilter.Text.Trim(); filterKind = KindFilter.SelectedIndex;
        ConfigureGrouping();
        IssuesList.ItemsSource = next.Errors; IssuesTab.Header = $"Issues ({next.Errors.Count:N0})";
        SummaryText.Text = $"{next.Groups.Count:N0} groups · {rowsByPath.Count:N0} matching videos";
        OutcomesList.ItemsSource = next.DeletionOutcomes.Select(o => $"{o.State}: {o.Path} — {o.Message}" + (o.AuditWarning == null ? "" : " · " + o.AuditWarning));
        OutcomesTab.Header = $"Deletion outcomes ({next.DeletionOutcomes.Count:N0})";
        StatusText.Text = next.Cancelled ? "Cancelled — completed work retained" : next.Status == ScanStatus.Failed ? "Scan failed — see Issues" : "Scan complete";
        DetailText.Text = $"{next.FileCount:N0} videos discovered. {next.Errors.Count:N0} issues. Name and visual results are candidates for review.";
        UpdateEmptyState(); UpdateSelection(); ExportButton.IsEnabled = !busy;
        BulkSelectButton.IsEnabled = !busy && next.Groups.Count > 0;
    }
    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (syncingSelection || e.PropertyName != nameof(ResultRow.Selected) || sender is not ResultRow row) return;
        syncingSelection = true;
        try
        {
            if (row.Selected) selectedPaths.Add(row.Path); else { selectedPaths.Remove(row.Path); discardPaths.Remove(row.Path); }
            foreach (var other in rowsByPath[row.Path]) if (other.Selected != row.Selected) other.Selected = row.Selected;
        }
        finally { syncingSelection = false; }
        UpdateGroupHeaders(rowsByPath[row.Path].Select(r => r.GroupId).Distinct());
        UpdateSelection(false);
    }
    private void UpdateSelection(bool refreshHeaders = true)
    {
        if (SelectionText == null) return;
        long size = selectedPaths.Sum(p => rowsByPath.TryGetValue(p, out var values) ? values[0].Member.File.Size : 0);
        SelectionText.Text = $"{selectedPaths.Count:N0} {(selectedPaths.Count == 1 ? "file" : "files")} selected for deletion · {Formatting.Size(size)}";
        DeleteButton.IsEnabled = selectedPaths.Count > 0 && !busy;
        if (refreshHeaders) UpdateGroupHeaders(groupHeaders.Keys);
    }
    private void UpdateGroupHeaders(IEnumerable<string> ids)
    {
        foreach (var id in ids)
            if (groupHeaders.TryGetValue(id, out var header))
            {
                int count = 0; long bytes = 0;
                foreach (var member in header.Group.Members) if (selectedPaths.Contains(member.File.Path)) { count++; bytes += member.File.Size; }
                header.HeaderSelection = $" · {count:N0} selected · {Formatting.Size(bytes)}";
            }
    }
    private bool FilterRow(object value)
    {
        var row = (ResultRow)value;
        if (row.IsGroupHeader) return visibleGroups.Contains(row.GroupId);
        return MatchesFilter(row) && (!grouped || !collapsedGroups.Contains(row.GroupId));
    }
    private bool MatchesFilter(ResultRow row)
    {
        int type = filterKind;
        return (type <= 0 || (int)row.Group.Kind == type - 1)
            && (filterText.Length == 0 || row.Path.Contains(filterText, StringComparison.OrdinalIgnoreCase));
    }
    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (TextFilter == null || KindFilter == null || ResultsGrid == null) return;
        filterTimer.Stop(); filterTimer.Start();
    }
    private void RefreshFilter()
    {
        string text = TextFilter.Text.Trim(); int kind = KindFilter.SelectedIndex;
        if (text == filterText && kind == filterKind) return;
        filterText = text; filterKind = kind;
        visibleGroups = rows.Where(MatchesFilter).Select(r => r.GroupId).ToHashSet(StringComparer.Ordinal);
        view?.Refresh(); UpdateEmptyState();
    }
    private void ConfigureGrouping()
    {
        grouped = GroupResultsBox.IsChecked == true;
        groupHeaders.Clear();
        var displayed = new List<ResultRow>(rows.Count + (grouped ? report?.Groups.Count ?? 0 : 0));
        if (grouped)
        {
            foreach (var group in rows.GroupBy(r => r.Group))
            {
                var header = new ResultRow { Group = group.Key, Member = group.First().Member, IsGroupHeader = true, Collapsed = collapsedGroups.Contains(group.Key.Id) };
                groupHeaders[group.Key.Id] = header; displayed.Add(header);
                displayed.AddRange(group);
            }
        }
        else displayed.AddRange(rows);
        visibleGroups = rows.Where(MatchesFilter).Select(r => r.GroupId).ToHashSet(StringComparer.Ordinal);
        view = CollectionViewSource.GetDefaultView(displayed); view.Filter = FilterRow; ResultsGrid.ItemsSource = view;
        foreach (var column in ResultsGrid.Columns) column.SortDirection = null;
        UpdateGroupHeaders(groupHeaders.Keys);
        UpdateEmptyState();
    }
    private void Grouping_Click(object sender, RoutedEventArgs e) => ConfigureGrouping();
    private void ToggleGroup_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is not ResultRow header) return;
        if (!collapsedGroups.Add(header.GroupId)) collapsedGroups.Remove(header.GroupId);
        header.Collapsed = collapsedGroups.Contains(header.GroupId); view?.Refresh();
    }
    private void Results_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (!grouped || view is not ListCollectionView list) return;
        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var column in ResultsGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = direction;
        string property = e.Column.SortMemberPath;
        if (property.Length == 0 && e.Column is DataGridBoundColumn bound && bound.Binding is Binding binding) property = binding.Path.Path;
        var order = report!.Groups.Select((g, i) => (g.Id, i)).ToDictionary(p => p.Id, p => p.i, StringComparer.Ordinal);
        list.CustomSort = new GroupRowComparer(order, property, direction);
    }
    private sealed class GroupRowComparer(Dictionary<string, int> order, string property, ListSortDirection direction) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var a = (ResultRow)x!; var b = (ResultRow)y!;
            int group = order[a.GroupId].CompareTo(order[b.GroupId]); if (group != 0) return group;
            if (a.IsGroupHeader || b.IsGroupHeader) return a.IsGroupHeader == b.IsGroupHeader ? 0 : a.IsGroupHeader ? -1 : 1;
            int value = property switch
            {
                nameof(ResultRow.SizeBytes) => a.SizeBytes.CompareTo(b.SizeBytes), nameof(ResultRow.ScoreValue) => a.ScoreValue.CompareTo(b.ScoreValue),
                nameof(ResultRow.DurationSeconds) => a.DurationSeconds.CompareTo(b.DurationSeconds), nameof(ResultRow.ResolutionPixels) => a.ResolutionPixels.CompareTo(b.ResolutionPixels),
                _ => StringComparer.OrdinalIgnoreCase.Compare(Text(a), Text(b))
            };
            return direction == ListSortDirection.Ascending ? value : -value;
        }
        private string Text(ResultRow row) => property switch
        {
            nameof(ResultRow.Path) => row.Path, nameof(ResultRow.Reason) => row.Reason, nameof(ResultRow.State) => row.State,
            nameof(ResultRow.Protection) => row.Protection, nameof(ResultRow.Codecs) => row.Codecs, nameof(ResultRow.MatchType) => row.MatchType,
            nameof(ResultRow.GroupId) => row.GroupId, _ => row.Filename
        };
    }
    private void UpdateEmptyState()
    {
        if (EmptyState == null) return;
        bool any = view?.Cast<ResultRow>().Any() == true;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (report != null) EmptyStateText.Text = rows.Count == 0 ? "No duplicate candidates found. Check Issues for skipped folders or videos." : "No results match this filter.";
    }
    private ResultRow? ActiveRow => ResultsGrid.SelectedItem is ResultRow { IsGroupHeader: false } row ? row : null;
    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveRow is not { } row || media == null || cache == null) { MessageBox.Show(this, "Select a result row first."); return; }
        if (row.Group.Members.Count < 2) { MessageBox.Show(this, "This group changed after deletion. Rescan to refresh its comparison evidence."); return; }
        // If the representative is selected, compare it to the first other member.
        if (row.Path.Equals(row.Group.Representative.Path, StringComparison.OrdinalIgnoreCase))
            row = new ResultRow { Group = row.Group, Member = row.Group.Members[1] };
        new EvidenceWindow(this, row, media, cache, report).ShowDialog(); ResultsGrid.Items.Refresh();
    }
    private void Results_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject original)
        {
            for (var parent = original; parent != null; parent = VisualTreeHelper.GetParent(parent)) if (parent is ButtonBase) return;
        }
        if (ActiveRow != null) Compare_Click(sender, e);
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveRow is { } row) TryOpen(row.Path);
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveRow is { } row) TryOpen(Path.GetDirectoryName(row.Path)!);
    }
    private void TryOpen(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open file or folder"); }
    }
    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        syncingSelection = true;
        foreach (var path in selectedPaths) foreach (var row in rowsByPath[path]) row.Selected = false;
        selectedPaths.Clear(); discardPaths.Clear(); syncingSelection = false; UpdateSelection();
    }
    private async void BulkSelect_Click(object sender, RoutedEventArgs e)
    {
        if (busy || report == null || view == null) return;
        ResultsGrid.CommitEdit(DataGridEditingUnit.Cell, true); ResultsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        filterTimer.Stop(); RefreshFilter();
        var visible = view.Cast<ResultRow>().Where(r => r.CanSelect).Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (KeepPolicyBox.SelectedIndex >= 4 && media != null && cache != null)
        {
            cancellation = new(); SetBusy(true); CancelButton.IsEnabled = true;
            try
            {
                var token = cancellation.Token;
                var files = rows.Where(r => visible.Contains(r.Path)).Select(r => r.Member.File).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
                await Task.Run(async () =>
                {
                    foreach (var file in files)
                    {
                        token.ThrowIfCancellationRequested();
                        if (file.Media != null) continue;
                        try { if (file.IsCurrent()) { file.Media = await media.ProbeAsync(file.Path, token); cache.Save(file); } }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or JsonException) { }
                    }
                }, token);
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { StatusText.Text = "Keep rule preparation cancelled"; return; }
            finally { cancellation.Dispose(); cancellation = null; SetBusy(false); }
        }
        var result = BulkSelection.Create(report, visible, (KeepCopyPolicy)Math.Max(0, KeepPolicyBox.SelectedIndex), keepPreferences);
        if (!arguments.Contains("--smoke") && new SelectionPreviewWindow(this, report, result.SelectedPaths).ShowDialog() != true) return; discardPaths.Clear();
        syncingSelection = true;
        try
        {
            selectedPaths.Clear(); selectedPaths.UnionWith(result.SelectedPaths);
            foreach (var row in rows)
            {
                bool select = row.CanSelect && selectedPaths.Contains(row.Path);
                if (row.Selected != select) row.Selected = select;
            }
        }
        finally { syncingSelection = false; }
        UpdateSelection();
        StatusText.Text = $"Selected {result.SelectedPaths.Count:N0} files across {result.Groups:N0} visible groups";
        DetailText.Text = $"{result.CopiesKept:N0} copies left unchecked. Overlapping searches can retain extra copies. Review the checkboxes before confirming deletion.";
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (report == null) return;
        var dialog = new SaveFileDialog { Filter = "CSV report (*.csv)|*.csv", FileName = $"SimilarVideoFinder-scan-{report.ScanId}.csv" };
        if (dialog.ShowDialog(this) == true)
        {
            try { ReportExporter.WriteCsv(dialog.FileName, report); StatusText.Text = "Report exported"; DetailText.Text = dialog.FileName; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export error"); }
        }
    }
    private void History_Click(object sender, RoutedEventArgs e)
    {
        if (cache == null) return;
        var history = cache.History();
        var window = new Window { Owner = this, Title = "Previous scans", Width = 700, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var grid = new Grid { Margin = new Thickness(20) }; grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var list = new ListBox { ItemsSource = history.Select(h => h.Label), SelectedIndex = history.Count > 0 ? 0 : -1 };
        grid.Children.Add(list);
        var button = new Button { Content = "Load selected results", Margin = new(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        button.Click += (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            var previous = cache.Report(history[list.SelectedIndex].Id);
            if (previous == null) MessageBox.Show(window, "This scan was interrupted before results were saved. Run the same search to reuse completed cached work.");
            else { ShowReport(previous); DetailText.Text = "Historical results. Rescan to refresh; deletion always checks that files are still current."; window.Close(); }
        };
        Grid.SetRow(button, 1); grid.Children.Add(button); window.Content = grid; window.ShowDialog();
    }
    private void ApplyOutcomes(IEnumerable<DeleteOutcome> outcomes)
    {
        foreach (var outcome in outcomes) if (rowsByPath.TryGetValue(outcome.Path, out var matches))
            foreach (var row in matches) row.State = outcome.State + ": " + outcome.Message;
    }
    private void RemoveDeleted(IEnumerable<DeleteOutcome> outcomes)
    {
        var removed = outcomes.Where(o => o.State is DeleteState.Recycled or DeleteState.DeletedPermanently or DeleteState.Quarantined).Select(o => o.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (report == null) return;
        ReportMaintenance.Prune(report, f => !removed.Contains(f.Path));
        foreach (var path in removed) cache!.Remove(path);
    }
    private void About_Click(object sender, RoutedEventArgs e)
    {
        var window = new Window { Owner = this, Title = "About / help", Width = 760, Height = 620, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Content = new TextBox { Text = "Similar Video Finder 3.0\n\nDelete whole group explicitly selects every eligible member and requires typed DELETE confirmation. Protected references stay protected. Normal cleanup requires direct retained matches.\n\nQuarantine copies selected files, verifies SHA-256 and writes restore manifests before removing originals. Use Restore quarantine to recover a copy; existing paths are never overwritten.\n\nScan / keep preferences configures exclusions, minimum size and duration, preferred folder, codec, dynamic range and language. Compare evidence supports any two group members, combined direct evidence, arbitrary preview positions and saved pair review decisions. Decisions persist only with history enabled. Manage cache clears history or removes stale records. The latest 30 scans are retained. Cancel operation stops between files.\n\nAdd folders and choose independent searches. SHA-256 confirms identical contents; name and visual results are candidates requiring review. Video matching targets complete re-encodes.\n\nReference folders are scanned and protected, including their subfolders. When reference folders are configured, only files with a direct match to a current reference can be deleted. Reference files cannot be selected, recycled or permanently deleted. Run a new scan after changing reference folders.\n\nSave history is a global switch, OFF by default. OFF uses an in-memory cache and does not save folders, media paths, reports, fingerprints, deletion audits or error logs. ON remembers these between sessions. Turning it off keeps old saved history on disk, but does not load or extend it. Explicit CSV exports are still available. Only the history and dark-mode switches are remembered while history is off.\n\nUse Dark mode in the header to change all windows. Collapse group headers or switch Group results off for a flat table. Sizes, durations, resolutions and scores sort numerically. Candidate percentages are algorithm scores, not probabilities.\n\nCompare evidence shows colour previews with zoom, audio and subtitle languages/tracks, bit depth and HDR information when available. Play both videos before deciding what to keep. File size alone is not a quality assessment.\n\nSelect all but one per group replaces the deletion selection using filtered results. Protected references are always retained. Overlapping groups can require additional retained copies. A path is selected everywhere it occurs.\n\nNetwork-share files go straight to permanent deletion after the first typed DELETE review, with no Recycle Bin attempt. Local files are recycled where supported; unsupported local files require separate typed confirmation. Files and retained references are revalidated before deletion. Deletion outcomes remain visible in their own tab, including audit failures.\n\nHistory location (only used when enabled): " + App.DataDirectory + "\n\n" + ToolBundle.Notices(),
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(20) };
        window.ShowDialog();
    }
    private void ClosingWindow(object? sender, CancelEventArgs e)
    {
        if (busy)
        {
            e.Cancel = true;
            if (cancellation != null && MessageBox.Show(this, App.SaveHistory ? "Cancel the scan, save completed work and close?" : "Cancel the scan and close? History is off, so session work will be discarded.", "Scan in progress", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            { closeRequested = true; cancellation.Cancel(); }
            else if (cancellation == null) MessageBox.Show(this, "Wait for the current file operation to finish before closing.");
        }
        else { settingsTimer.Stop(); filterTimer.Stop(); SaveCurrentSettings(); cache?.Dispose(); }
    }
    private string? Arg(string key) { int i = Array.IndexOf(arguments, key); return i >= 0 && i + 1 < arguments.Length ? arguments[i + 1] : null; }
    private static void SaveImage(Window window, string path)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        double width = content.ActualWidth + content.Margin.Left + content.Margin.Right;
        double height = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
