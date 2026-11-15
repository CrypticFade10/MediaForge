using MediaForge.Tooling;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SimilarVideoFinder.App.Shell;

internal static class SuiteSmoke
{
    private sealed class FixtureTool : IToolModule
    {
        public string Id { get; init; } = "fixture";
        public string Name => "Fixture";
        public string Description => "Test module";
        public string Category => "Tests";
        public bool Fail { get; set; }
        public ToolContext? Context { get; private set; }
        public Window CreateWindow(ToolContext context)
        {
            Context = context;
            if (Fail) throw new InvalidOperationException("Expected fixture failure");
            return new Window { Width = 200, Height = 100, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        }
    }

    public static async void Run(SuiteWindow shell, ToolRegistry registry)
    {
        var checks = new List<string>();
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            checks.Add(label);
        }
        try
        {
            await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(registry.Modules.Count == 1 && registry.Find("DUPLICATE-FINDER") != null, "Built-in registration and case-insensitive lookup");
            foreach (string id in new[] { "", "../escape", "with space" })
            {
                bool rejected = false;
                try { _ = new ToolRegistry([new FixtureTool { Id = id }]); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Reject invalid ID: " + id);
            }
            bool duplicateRejected = false;
            try { _ = new ToolRegistry([new FixtureTool(), new FixtureTool { Id = "FIXTURE" }]); } catch (ArgumentException) { duplicateRejected = true; }
            Check(duplicateRejected, "Reject duplicate module IDs");

            var search = (TextBox)shell.FindName("SearchBox");
            var cards = (ItemsControl)shell.FindName("ToolCards");
            search.Text = "does-not-exist";
            Check(cards.Items.Count == 0, "Tool search excludes unrelated tools");
            search.Text = "library";
            Check(cards.Items.Count == 1, "Tool search matches categories");
            search.Text = "";
            foreach (bool dark in new[] { false, true })
            {
                ThemeManager.Apply(dark); ((CheckBox)shell.FindName("DarkModeToggle")).IsChecked = dark; shell.UpdateLayout();
                var content = (FrameworkElement)shell.Content;
                var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(App.DataDirectory, dark ? "suite-dark.png" : "suite-light.png")); encoder.Save(file);
            }
            ThemeManager.Apply(App.DarkMode);
            var window = shell.OpenTool("duplicate-finder");
            Check(ReferenceEquals(window, shell.OpenTool("DUPLICATE-FINDER")), "Opening a running tool reuses its window");
            window.WindowState = WindowState.Minimized;
            shell.OpenTool("duplicate-finder");
            Check(window.WindowState == WindowState.Normal, "Reopening restores a minimised window");
            var scanButton = (Button)window.FindName("ScanButton");
            for (int i = 0; i < 150 && !scanButton.IsEnabled; i++) await Task.Delay(200);
            Check(scanButton.IsEnabled, "Duplicate Finder initialises through launcher with bundled media tools");
            window.Close();
            var reopened = shell.OpenTool("duplicate-finder");
            Check(!ReferenceEquals(window, reopened), "Closed tools can be opened again");
            var reopenedScan = (Button)reopened.FindName("ScanButton");
            for (int i = 0; i < 150 && !reopenedScan.IsEnabled; i++) await Task.Delay(200);
            Check(reopenedScan.IsEnabled, "Reopened tool initialises successfully");
            reopened.Close();

            var fixture = new FixtureTool { Fail = true };
            var fixtureShell = new SuiteWindow(new([fixture]), ["--suite-smoke"]);
            fixtureShell.Show();
            bool failed = false;
            try { fixtureShell.OpenTool("fixture"); } catch (InvalidOperationException) { failed = true; }
            Check(failed, "Failed module creation is reported");
            fixture.Fail = false;
            var fixtureWindow = fixtureShell.OpenTool("fixture");
            Check(fixtureWindow.IsVisible, "Module creation can be retried after failure");
            Check(fixture.Context?.DataDirectory == Path.Combine(App.DataDirectory, "modules", "fixture"), "New tools receive isolated data directories");
            fixtureWindow.Close(); fixtureShell.Close();
            File.WriteAllText(Path.Combine(App.DataDirectory, "suite-smoke.json"), JsonSerializer.Serialize(new { Success = true, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(App.DataDirectory, "suite-smoke.json"), JsonSerializer.Serialize(new { Success = false, Checks = checks, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(1);
        }
    }
}
