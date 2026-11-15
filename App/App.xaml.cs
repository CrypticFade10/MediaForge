using System.IO;
using System.Windows;
using System.Text.Json;

namespace SimilarVideoFinder.App;

public partial class App : Application
{
    public static string DataDirectory { get; private set; } = "";
    public static bool SaveHistory { get; private set; }
    public static bool DarkMode { get; private set; }
    private sealed record Preferences(bool SaveHistory = false, bool DarkMode = false);
    public static void SetPreferences(bool history, bool dark)
    {
        string path = Path.Combine(DataDirectory, "preferences.json");
        // This file contains only two switches, never paths, filenames, reports or logs.
        string temporary = path + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(new Preferences(history, dark))); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        SaveHistory = history; DarkMode = dark; ThemeManager.Apply(dark);
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var dataArg = Array.IndexOf(e.Args, "--data-dir");
        DataDirectory = dataArg >= 0 && dataArg + 1 < e.Args.Length ? Path.GetFullPath(e.Args[dataArg + 1])
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimilarVideoFinder");
        Directory.CreateDirectory(DataDirectory);
        try
        {
            var path = Path.Combine(DataDirectory, "preferences.json");
            var preferences = File.Exists(path) ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) : null;
            SaveHistory = preferences?.SaveHistory == true; DarkMode = preferences?.DarkMode == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { SaveHistory = false; DarkMode = false; }
        ThemeManager.Apply(DarkMode);
        Branding.Apply();
        DispatcherUnhandledException += (_, ex) =>
        {
            if (SaveHistory) try { File.AppendAllText(Path.Combine(DataDirectory, "errors.log"), DateTime.UtcNow.ToString("O") + " " + ex.Exception + Environment.NewLine); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            MessageBox.Show(ex.Exception.Message, "MediaForge", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        var registry = Modules.BuiltInTools.CreateRegistry();
        int toolArgument = Array.IndexOf(e.Args, "--tool");
        string? toolId = toolArgument >= 0 && toolArgument + 1 < e.Args.Length ? e.Args[toolArgument + 1] : null;
        if (toolArgument >= 0 && (toolId == null || registry.Find(toolId) == null))
        {
            MessageBox.Show("Use --tool followed by an installed tool ID: " + string.Join(", ", registry.Modules.Select(t => t.Id)), "MediaForge");
            Shutdown(1); return;
        }
        if (e.Args.Contains("--smoke") || toolId != null)
        {
            var module = registry.Find(toolId ?? "duplicate-finder")!;
            string moduleData = module.Id == "duplicate-finder" ? DataDirectory : Path.Combine(DataDirectory, "modules", module.Id);
            Directory.CreateDirectory(moduleData);
            var tool = module.CreateWindow(new(moduleData, Array.AsReadOnly(e.Args)));
            MainWindow = tool; tool.Show();
        }
        else
        {
            var shell = new Shell.SuiteWindow(registry, e.Args); MainWindow = shell; shell.Show();
            if (e.Args.Contains("--suite-smoke")) Shell.SuiteSmoke.Run(shell, registry);
        }
    }
}
