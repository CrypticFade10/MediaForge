using MediaForge.Tooling;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace SimilarVideoFinder.App.Subtitles;

public partial class ForcedSubtitlesWindow : Window
{
    private readonly ToolContext context;
    private CancellationTokenSource? cancellation;
    private bool closeAfterRun;
    private string ModelsDirectory => Path.Combine(context.DataDirectory, "models");

    public ForcedSubtitlesWindow(ToolContext context)
    {
        this.context = context;
        InitializeComponent();
        Closing += OnClosing;
        if (context.Arguments.Contains("--suite-smoke"))
        {
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -10000;
            Top = -10000;
        }
    }

    private void BrowseVideo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Video files|*.mkv;*.mp4;*.m4v;*.avi;*.mov;*.wmv;*.ts;*.m2ts;*.webm;*.mpg;*.mpeg|All files|*.*"
        };
        if (dialog.ShowDialog(this) == true) InputBox.Text = dialog.FileName;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog(this) == true) InputBox.Text = dialog.FolderName;
    }

    private void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ModelsDirectory);
            var dialog = new OpenFolderDialog
            {
                Title = "Choose a CTranslate2 Whisper model folder",
                InitialDirectory = Directory.Exists(LocalModelBox.Text) ? LocalModelBox.Text : ModelsDirectory
            };
            if (dialog.ShowDialog(this) == true) LocalModelBox.Text = dialog.FolderName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void OpenModels_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ModelsDirectory);
            Process.Start(new ProcessStartInfo(ModelsDirectory) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (cancellation != null) return;
        string input = InputBox.Text.Trim().Trim('"');
        string python = PythonBox.Text.Trim().Trim('"');
        if (input.Length == 0 || python.Length == 0 ||
            !int.TryParse(TrackBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int track))
        {
            StatusText.Text = "Choose a video or folder, a Python executable and a valid audio track number.";
            return;
        }

        string localModel = LocalModelBox.Text.Trim().Trim('"');
        string model = (string)((ComboBoxItem)ModelBox.SelectedItem).Content;
        string device = DeviceBox.SelectedIndex == 1 ? "cuda" : "cpu";
        string workDirectory = Path.Combine(context.DataDirectory, "work-" + Guid.NewGuid().ToString("N"));
        using var run = new CancellationTokenSource();
        cancellation = run;
        OptionsPanel.IsEnabled = false;
        StartButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        LogBox.Clear();
        StatusText.Text = "Preparing subtitle tools…";
        try
        {
            string ffmpeg = await Task.Run(() => ToolBundle.Prepare(App.DataDirectory).FFmpegPath);
            run.Token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(workDirectory);
            string script = Path.Combine(workDirectory, "forced_subtitles.py");
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Tools.forced_subtitles.py")
                ?? throw new IOException("The subtitle worker is missing."))
            using (var file = File.Create(script))
                resource.CopyTo(file);

            var start = new ProcessStartInfo(python)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workDirectory
            };
            foreach (string argument in new[] { "-X", "utf8", "-u", script, Path.GetFullPath(input),
                "--ffmpeg", ffmpeg, "--ffprobe", Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe"),
                "--work-dir", workDirectory, "--device", device,
                "--audio-track", track.ToString(CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(argument);
            if (localModel.Length == 0)
            {
                start.ArgumentList.Add("--model");
                start.ArgumentList.Add(model);
            }
            else
            {
                start.ArgumentList.Add("--model-dir");
                start.ArgumentList.Add(Path.GetFullPath(localModel));
            }

            using var process = Process.Start(start) ?? throw new IOException("Unable to start Python.");
            using var registration = run.Token.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            });
            StatusText.Text = "Generating subtitles…";
            var stderr = process.StandardError.ReadToEndAsync();
            while (await process.StandardOutput.ReadLineAsync() is string line)
                AppendLog(line);
            await process.WaitForExitAsync();
            string error = await stderr;
            if (!string.IsNullOrWhiteSpace(error)) AppendLog(error.Trim());
            run.Token.ThrowIfCancellationRequested();
            StatusText.Text = process.ExitCode == 0 ? "Finished. See the log for results." : "Finished with errors. See the log for details.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Cancelled. Completed SRTs were kept."; }
        catch (Exception ex)
        {
            StatusText.Text = "Unable to run the subtitle generator.";
            AppendLog(ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, recursive: true); }
            catch (IOException ex) { AppendLog(ex.Message); }
            catch (UnauthorizedAccessException ex) { AppendLog(ex.Message); }
            cancellation = null;
            OptionsPanel.IsEnabled = true;
            StartButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            if (closeAfterRun) Close();
        }
    }

    private void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        cancellation?.Cancel();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (cancellation == null) return;
        e.Cancel = true;
        closeAfterRun = true;
        cancellation.Cancel();
    }
}
