using MediaForge.Integrity;
using MediaForge.Tooling;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace SimilarVideoFinder.App.Integrity;

public partial class IntegrityWindow : Window
{
    private readonly ToolContext context;
    private readonly ObservableCollection<string> folders = [];
    private readonly ObservableCollection<IntegrityResult> results = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<IntegrityResult> pending = new();
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private CancellationTokenSource? cancellation;
    private IntegrityScanner? scanner;
    private IntegrityReport? report;
    private ICollectionView? view;
    private IntegrityProgress? latestProgress;
    private bool closeWhenStopped;
    private readonly Stopwatch elapsed = new();

    public IntegrityWindow(ToolContext context)
    {
        this.context = context; InitializeComponent();
        FolderList.ItemsSource = folders; ExtensionsBox.Text = new IntegrityOptions().Extensions;
        view = CollectionViewSource.GetDefaultView(results); view.Filter = MatchesFilter; ResultsGrid.ItemsSource = view;
        refreshTimer.Tick += (_, _) => Flush();
        Loaded += PrepareAsync;
        Closing += OnClosing;
        Closed += (_, _) => { refreshTimer.Stop(); cancellation?.Dispose(); };
        if (context.Arguments.Contains("--suite-smoke")) { ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.Manual; Left = -10000; Top = -10000; }
    }

    private async void PrepareAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            var media = await Task.Run(() => ToolBundle.Prepare(App.DataDirectory));
            scanner = new(media.FFmpegPath, Path.Combine(Path.GetDirectoryName(media.FFmpegPath)!, "ffprobe.exe"));
            if (IsLoaded) { StartButton.IsEnabled = true; ProgressText.Text = "Ready — choose folders to check."; }
        }
        catch (Exception ex) { ProgressText.Text = "Unable to prepare video tools: " + ex.Message; }
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to check", Multiselect = true };
        if (dialog.ShowDialog(this) == true) foreach (var folder in dialog.FolderNames) AddFolder(folder);
    }
    private void AddFolder(string path)
    {
        try
        {
            string full = Path.GetFullPath(path.Trim().Trim('"'));
            if (!folders.Contains(full, StringComparer.OrdinalIgnoreCase)) folders.Add(full);
            FolderPathBox.Clear();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { MessageBox.Show(this, ex.Message, "Folder path"); }
    }
    private void AddPath_Click(object sender, RoutedEventArgs e) { if (!string.IsNullOrWhiteSpace(FolderPathBox.Text)) AddFolder(FolderPathBox.Text); }
    private void Path_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) AddPath_Click(sender, e); }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (FolderList.SelectedItem is string path) folders.Remove(path); }
    private async void Start_Click(object sender, RoutedEventArgs e) => await StartAsync();
    private async Task StartAsync()
    {
        if (scanner == null || cancellation != null) return;
        if (folders.Count == 0) { MessageBox.Show(this, "Add at least one folder to check.", "Media Integrity Checker"); return; }
        if (!int.TryParse(TimeoutBox.Text, out int timeout) || timeout is < 10 or > 3600) { MessageBox.Show(this, "Choose a stall timeout between 10 and 3600 seconds."); return; }
        if (string.IsNullOrWhiteSpace(ExtensionsBox.Text)) { MessageBox.Show(this, "Enter at least one media extension."); return; }
        var options = new IntegrityOptions { Roots = folders.ToList(), Extensions = ExtensionsBox.Text, Mode = ModeBox.SelectedIndex == 0 ? CheckMode.FullDecode : CheckMode.QuickInspection, Decoder = DecoderBox.SelectedIndex == 0 ? DecodeMode.Cpu : DecodeMode.Gpu, Workers = WorkersBox.SelectedIndex + 1, StallTimeoutSeconds = timeout };
        results.Clear(); report = null; latestProgress = null;
        cancellation = new(); OptionsPanel.IsEnabled = false; StartButton.IsEnabled = false; CancelButton.IsEnabled = true; ExportButton.IsEnabled = false;
        elapsed.Restart(); refreshTimer.Start();
        try
        {
            // Progress is sampled by the UI timer; file callbacks never block decoder workers.
            var progress = new CallbackProgress<IntegrityProgress>(p => Volatile.Write(ref latestProgress, p));
            report = await Task.Run(() => scanner.ScanAsync(options, progress, pending.Enqueue, cancellation.Token));
        }
        catch (Exception ex) { ProgressText.Text = "Unable to complete scan: " + ex.Message; DiagnosticsBox.Text = ex.ToString(); }
        finally
        {
            elapsed.Stop(); Flush(); refreshTimer.Stop(); FileProgress.IsIndeterminate = false;
            cancellation.Dispose(); cancellation = null;
            OptionsPanel.IsEnabled = true; StartButton.IsEnabled = true; CancelButton.IsEnabled = false; ExportButton.IsEnabled = results.Count > 0;
            if (report != null) SummaryText.Text = (report.Cancelled ? "Cancelled. Unstarted files were not checked. " : "Completed. ") + Counts() + " Results remain in memory until exported.";
            if (closeWhenStopped) Close();
        }
    }
    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T> { public void Report(T value) => callback(value); }
    private void Flush()
    {
        while (pending.TryDequeue(out var result)) results.Add(result);
        var p = Volatile.Read(ref latestProgress);
        if (p != null)
        {
            ProgressText.Text = $"{p.Stage} · {p.Completed:N0}/{p.Discovered:N0} files · {elapsed.Elapsed:hh\\:mm\\:ss} · {p.Decoder} {p.Path}";
            FileProgress.IsIndeterminate = p.Stage == "Discovering folders" || p.Percent == null && cancellation != null && p.Path.Length > 0;
            FileProgress.Value = p.Percent ?? (p.Discovered > 0 ? 100d * p.Completed / p.Discovered : 0);
        }
        CountText.Text = $"{results.Count:N0} results";
        SummaryText.Text = Counts() + " Results stay in memory until exported.";
    }
    private string Counts() => string.Join(" · ", results.GroupBy(r => r.StatusText).OrderBy(g => g.Key).Select(g => $"{g.Count():N0} {g.Key.ToLowerInvariant()}"));
    private bool MatchesFilter(object row) => row is IntegrityResult r && FilterBox.SelectedIndex switch
    {
        0 => r.Status != IntegrityStatus.Passed, 2 => r.Status == IntegrityStatus.MediaErrors,
        3 => r.Status == IntegrityStatus.Warnings, 4 => r.Status == IntegrityStatus.Passed, _ => true
    };
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => view?.Refresh();
    private void Result_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is IntegrityResult result) DiagnosticsBox.Text = result.Path + "\n\n" + result.Summary + "\n\n" + result.Diagnostics;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { cancellation?.Cancel(); CancelButton.IsEnabled = false; ProgressText.Text = "Cancelling decoder processes…"; }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (context.Arguments.Contains("--suite-smoke")) { cancellation?.Cancel(); return; }
        if (cancellation == null) return;
        e.Cancel = true;
        if (MessageBox.Show(this, "Cancel the current check and close? Export any results you want to keep before closing.", "Check in progress", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        { closeWhenStopped = true; cancellation.Cancel(); }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV report|*.csv|JSON report|*.json", FileName = "MediaForge-integrity-report", AddExtension = true };
        if (dialog.ShowDialog(this) != true || report == null) return;
        try
        {
            if (dialog.FilterIndex == 2) IntegrityExport.WriteJson(dialog.FileName, report);
            else IntegrityExport.WriteCsv(dialog.FileName, report.Results);
            SummaryText.Text = "Exported all results to " + dialog.FileName;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to export report"); }
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not IntegrityResult result) return;
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add("/select," + result.Path); Process.Start(start);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open containing folder"); }
    }

    internal async Task<bool> RunSmokeAsync()
    {
        string fixture = Path.Combine(context.DataDirectory, "smoke-fixtures"); Directory.CreateDirectory(fixture);
        var media = await Task.Run(() => ToolBundle.Prepare(App.DataDirectory));
        await SimilarVideoFinder.Core.MediaProcessor.RunAsync(media.FFmpegPath,
            ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "0.25", "-c:a", "aac", Path.Combine(fixture, "healthy.m4a")], CancellationToken.None);
        File.WriteAllText(Path.Combine(fixture, "damaged.mkv"), "Not a media file.");
        AddFolder(fixture);
        await StartAsync();
        bool success = report is { Cancelled: false } && results.Count == 2 && results.Any(r => r.Status == IntegrityStatus.Passed) && results.Any(r => r.Status == IntegrityStatus.MediaErrors) && view?.Cast<object>().Count() == 1;
        FilterBox.SelectedIndex = 1; success &= view?.Cast<object>().Count() == 2;
        ResultsGrid.SelectedItem = results.First(r => r.Status == IntegrityStatus.MediaErrors);
        success &= DiagnosticsBox.Text.Contains("damaged.mkv") && ExportButton.IsEnabled;
        if (report != null) { IntegrityExport.WriteCsv(Path.Combine(context.DataDirectory, "smoke-report.csv"), report.Results); IntegrityExport.WriteJson(Path.Combine(context.DataDirectory, "smoke-report.json"), report); }
        return success;
    }
}
