using Microsoft.Win32;
using SimilarVideoFinder.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace SimilarVideoFinder.App;

public partial class MainWindow
{
    public static System.Windows.Input.RoutedUICommand DiscardGroupCommand { get; } = new("Delete whole group", "DiscardGroup", typeof(MainWindow));
    private readonly HashSet<string> discardPaths = new(StringComparer.OrdinalIgnoreCase);
    private ScanOptions extraOptions = new();
    private KeepPreferences keepPreferences = new();
    private bool quarantineRequested;
    private string quarantineFolder = "";
    private void ReviewPreferences_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        new ReviewSettingsWindow(this, Options(false), keepPreferences, (options, preferences) =>
        { extraOptions = options; keepPreferences = preferences; SaveCurrentSettings(); }).ShowDialog();
    }
    private void WholeGroup_Click(object sender, RoutedEventArgs e)
    {
        if (busy || report == null) return;
        var group = (sender as FrameworkElement)?.Tag as MatchGroup ?? (ResultsGrid.SelectedItem as ResultRow)?.Group;
        if (group == null) { MessageBox.Show(this, "Select a group or result row first."); return; }
        var members = group.Members.Select(m => m.File).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (members.Any(f => ReferencePolicy.Protected(report, f)) || members.Any(f => !rowsByPath[f.Path].Any(r => r.CanSelect)))
        { MessageBox.Show(this, "This group contains protected or ineligible files. Protected references cannot be discarded."); return; }
        if (MessageBox.Show(this, $"Select all {members.Length} files in group {group.Id}? No copy is required to remain for these files. Selection also applies in overlapping searches. You will review the full paths before deletion.", "Delete whole group", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var file in members) { discardPaths.Add(file.Path); rowsByPath[file.Path][0].Selected = true; }
        UpdateSelection();
    }
    private async void CacheManage_Click(object sender, RoutedEventArgs e)
    {
        if (busy || cache == null) return;
        var stats = cache.Statistics();
        var window = new Window { Owner = this, Title = "History and cache", Width = 540, Height = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24) };
        var label = new TextBlock { Text = $"{stats.Files:N0} cached files; {stats.Scans} scans; {stats.Reviews} pair decisions\nDatabase pages: {Formatting.Size(stats.Bytes)}\nThe latest 30 scans are retained. History off uses session memory.", TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(label);
        var stale = new Button { Content = "Remove stale file records", Margin = new(0, 15, 0, 0) };
        stale.Click += async (_, _) => { stale.IsEnabled = false; try { int count = await Task.Run(cache.RemoveStaleFiles); label.Text = $"Removed {count:N0} stale records."; } catch (Exception ex) { label.Text = ex.Message; } finally { stale.IsEnabled = true; } };
        panel.Children.Add(stale);
        var clear = new Button { Content = "Clear history, cache and review decisions", Margin = new(0, 10, 0, 0) };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(window, "Clear saved scans, settings, cache, pair decisions and deletion audit? Videos and quarantine copies are unaffected.", "Clear history", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            try { cache.ClearHistory(); if (App.SaveHistory) { foreach (string name in new[] { "deletions.jsonl", "errors.log" }) { string path = Path.Combine(App.DataDirectory, name); if (File.Exists(path)) File.Delete(path); } } label.Text = "History cleared."; }
            catch (Exception ex) { label.Text = ex.Message; }
        };
        panel.Children.Add(clear); window.Content = panel; window.ShowDialog(); await Task.CompletedTask;
    }
    private void Quarantine_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var picker = new OpenFolderDialog { Title = "Choose quarantine folder (verified copies and restore manifests)" };
        if (picker.ShowDialog(this) != true) return;
        quarantineFolder = picker.FolderName; quarantineRequested = true;
        Delete_Click(sender, e);
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var picker = new OpenFileDialog { Title = "Choose quarantine restore manifest", Filter = "Restore manifest (*.restore.json)|*.restore.json" };
        if (picker.ShowDialog(this) != true) return;
        SetBusy(true);
        try { string path = await Task.Run(() => QuarantineBackend.Restore(picker.FileName)); StatusText.Text = "Quarantine copy restored"; DetailText.Text = path; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Restore failed"); }
        finally { SetBusy(false); }
    }
}
