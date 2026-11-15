using MediaForge.Tooling;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SimilarVideoFinder.App.Shell;

public partial class SuiteWindow : Window
{
    private readonly ToolRegistry registry;
    private readonly string[] arguments;
    private readonly Dictionary<string, Window> openTools = new(StringComparer.OrdinalIgnoreCase);

    public SuiteWindow(ToolRegistry registry, string[] arguments)
    {
        this.registry = registry;
        this.arguments = arguments;
        InitializeComponent();
        if (arguments.Contains("--suite-smoke"))
        { ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.Manual; Left = -10000; Top = -10000; }
        DarkModeToggle.IsChecked = App.DarkMode;
        RefreshTools();
        Activated += (_, _) => DarkModeToggle.IsChecked = App.DarkMode;
        Closing += OnClosing;
    }

    public Window OpenTool(string id)
    {
        if (openTools.TryGetValue(id, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }
        var module = registry.Find(id) ?? throw new ArgumentException($"Unknown tool: {id}");
        // Preserve existing Duplicate Finder settings; future tools have separate storage.
        string dataDirectory = module.Id == "duplicate-finder" ? App.DataDirectory : Path.Combine(App.DataDirectory, "modules", module.Id);
        Directory.CreateDirectory(dataDirectory);
        var window = module.CreateWindow(new(dataDirectory, Array.AsReadOnly(arguments)))
            ?? throw new InvalidOperationException($"{module.Name} did not create a window.");
        openTools.Add(module.Id, window);
        window.Closed += (_, _) => { openTools.Remove(module.Id); RefreshStatus(); };
        try { window.Show(); }
        catch { openTools.Remove(module.Id); throw; }
        RefreshStatus();
        return window;
    }

    private void OpenTool_Click(object sender, RoutedEventArgs e)
    {
        try { OpenTool((string)((Button)sender).Tag); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to open tool", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (ToolCards != null) RefreshTools();
    }

    private void RefreshTools()
    {
        string query = SearchBox.Text.Trim();
        var tools = registry.Modules.Where(m => $"{m.Name} {m.Description} {m.Category}".Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        ToolCards.ItemsSource = tools;
        EmptyText.Visibility = tools.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshStatus();
    }

    private void RefreshStatus() => StatusText.Text = $"{registry.Modules.Count} tool(s) available · {openTools.Count} open";

    private void DarkMode_Click(object sender, RoutedEventArgs e)
    {
        try { App.SetPreferences(App.SaveHistory, DarkModeToggle.IsChecked == true); }
        catch (Exception ex) { DarkModeToggle.IsChecked = App.DarkMode; MessageBox.Show(this, ex.Message, "Theme preference"); }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (arguments.Contains("--suite-smoke")) return;
        // Never bypass a module's scan/deletion close safeguards through owner shutdown.
        if (openTools.Count == 0) return;
        e.Cancel = true;
        MessageBox.Show(this, "Close your open tools before exiting MediaForge. You can return to this launcher at any time.", "Tools are open", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
