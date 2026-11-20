using MediaForge.Tooling;
using MediaForge.SubtitleModels;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace SimilarVideoFinder.App.Subtitles;

public partial class ForcedSubtitlesWindow : Window
{
    private readonly ToolContext context;
    private CancellationTokenSource? cancellation;
    private bool closeAfterRun;
    private int modelStatusRevision;
    private static readonly HttpClient ModelHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private string ModelsDirectory => Path.GetFullPath(ModelStorageBox.Text.Trim().Trim('"'));
    private string SelectedPreset => (string)((ComboBoxItem)ModelBox.SelectedItem).Content;

    public ForcedSubtitlesWindow(ToolContext context)
    {
        this.context = context;
        InitializeComponent();
        ModelStorageBox.Text = WhisperModels.DefaultModelsDirectory(AppContext.BaseDirectory);
        RefreshModelStatus();
        Closing += OnClosing;
        if (context.Arguments.Contains("--suite-smoke"))
        {
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -10000;
            Top = -10000;
        }
    }

    private void ModelSelection_Changed(object sender, SelectionChangedEventArgs e) => RefreshModelStatus();
    private void ModelStorage_Changed(object sender, TextChangedEventArgs e) => RefreshModelStatus();
    private async void RefreshModelStatus()
    {
        if (ModelStatusText == null || ModelStorageBox == null || ModelBox?.SelectedItem == null) return;
        int revision = ++modelStatusRevision;
        try
        {
            string root = ModelsDirectory, preset = SelectedPreset;
            var installed = await Task.Run(() => WhisperModels.FindInstalled(root, preset));
            if (revision == modelStatusRevision)
                ModelStatusText.Text = installed == null ? $"{preset} is not downloaded in this folder." : $"{preset} is ready for offline use.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { if (revision == modelStatusRevision) ModelStatusText.Text = "Choose a writable model download folder. " + ex.Message; }
    }

    private void BrowseStorage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where downloaded Whisper models are stored" };
        if (dialog.ShowDialog(this) == true) ModelStorageBox.Text = dialog.FolderName;
    }

    private Progress<ModelDownloadProgress> DownloadUpdates() => new(p =>
    {
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgressText.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = p.Percent == null;
        DownloadProgress.Value = p.Percent ?? 0;
        string size = p.TotalBytes > 0 ? $" — {p.ReceivedBytes / 1048576d:N1} / {p.TotalBytes / 1048576d:N1} MiB ({p.Percent:N0}%)" : "";
        DownloadProgressText.Text = p.Stage + ": " + p.File + size;
        StatusText.Text = p.Stage + "…";
    });

    private async Task<string> EnsurePresetAsync(bool checkServer, CancellationToken token)
    {
        string preset = SelectedPreset, root = ModelsDirectory;
        var installed = await Task.Run(() => WhisperModels.FindInstalled(root, preset), token);
        if (!checkServer && installed != null) { AppendLog($"Using downloaded {preset}: {installed.Directory}"); return installed.Directory; }
        AppendLog($"Downloading {preset} into {root}. Media files are not uploaded.");
        var updates = DownloadUpdates();
        var model = await Task.Run(() => new WhisperModelDownloader(ModelHttp).DownloadAsync(preset, root, updates, token), token);
        AppendLog($"Model verified and ready: {model.Directory}");
        return model.Directory;
    }

    private void SetBusy(bool busy)
    {
        OptionsPanel.IsEnabled = !busy; StartButton.IsEnabled = !busy; CancelButton.IsEnabled = busy;
        if (!busy) DownloadProgress.IsIndeterminate = false;
        if (busy) { DownloadProgress.Visibility = Visibility.Collapsed; DownloadProgressText.Visibility = Visibility.Collapsed; }
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        if (cancellation != null) return;
        using var run = new CancellationTokenSource(); cancellation = run;
        SetBusy(true); LogBox.Clear();
        try
        {
            string directory = await EnsurePresetAsync(true, run.Token);
            run.Token.ThrowIfCancellationRequested();
            LocalModelBox.Clear();
            StatusText.Text = $"{SelectedPreset} is downloaded and ready. Select a video to generate subtitles.";
            ModelStatusText.Text = directory;
        }
        catch (OperationCanceledException) { StatusText.Text = "Model download cancelled. Previously installed models were kept."; }
        catch (Exception ex) { StatusText.Text = "Unable to download the model. See the log and retry."; AppendLog(ex.Message); }
        finally { cancellation = null; SetBusy(false); RefreshModelStatus(); if (closeAfterRun) Close(); }
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
        string device = DeviceBox.SelectedIndex == 1 ? "cuda" : "cpu";
        string workDirectory = Path.Combine(WhisperModels.WorkDirectory(AppContext.BaseDirectory), "forced-subtitles-" + Guid.NewGuid().ToString("N"));
        using var run = new CancellationTokenSource();
        cancellation = run;
        SetBusy(true);
        LogBox.Clear();
        StatusText.Text = "Preparing subtitle tools…";
        try
        {
            if (localModel.Length == 0) localModel = await EnsurePresetAsync(false, run.Token);
            if (!Directory.Exists(localModel)) throw new IOException("The selected local model folder does not exist.");
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
            start.ArgumentList.Add("--model-dir");
            start.ArgumentList.Add(Path.GetFullPath(localModel));
            start.Environment["HF_HUB_OFFLINE"] = "1";
            start.Environment["TRANSFORMERS_OFFLINE"] = "1";

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
            SetBusy(false); RefreshModelStatus();
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
