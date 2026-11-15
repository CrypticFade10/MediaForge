using SimilarVideoFinder.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SimilarVideoFinder.App;

public sealed class ConfirmationWindow : Window
{
    public ConfirmationWindow(Window owner, IReadOnlyList<FileRecord> files, bool permanent, int networkFiles = 0, int discardFiles = 0, IReadOnlyDictionary<string, string>? review = null, bool quarantine = false)
    {
        bool typedConfirmation = permanent || networkFiles > 0 || discardFiles > 0;
        bool allPermanent = permanent || networkFiles == files.Count;
        Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; Width = 760; Height = 560;
        MinWidth = 600; MinHeight = 450; Title = typedConfirmation ? "Confirm deletion" : "Review selected files";
        var grid = new Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = quarantine ? "Move verified copies to quarantine?" : allPermanent ? "Permanently delete these files?" : "Review before deleting", FontSize = 25, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = $"{files.Count:N0} files · {Formatting.Size(files.Sum(f => f.Size))}", Margin = new(0, 8, 0, 8) });
        heading.Children.Add(new TextBlock { Text = networkFiles > 0 ? $"{networkFiles:N0} network file(s) will be deleted permanently, without a Recycle Bin attempt. This cannot be undone by the app." +
            (networkFiles < files.Count ? " Local files will be recycled where supported; any local permanent fallback requires another confirmation." : "") : permanent ? "These files cannot be recycled. This action cannot be undone by the app."
            : "Local files will be sent to the Windows Recycle Bin. Files that cannot be recycled require another confirmation.", TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(permanent ? Color.FromRgb(154, 57, 31) : Color.FromRgb(75, 105, 114)), Margin = new(0, 0, 0, 16) });
        grid.Children.Add(heading);
        ThemeManager.Bind(heading.Children[2] as FrameworkElement ?? heading, TextBlock.ForegroundProperty, typedConfirmation ? "WarningBrush" : "MutedBrush");
        if (discardFiles > 0) heading.Children.Add(new TextBlock { Text = $"{discardFiles} file(s) explicitly marked for whole-group discard. No surviving copy is required.", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.Bold });
        if (quarantine) ((TextBlock)heading.Children[2]).Text = "Files are copied and SHA-256 verified before the originals are removed. Each copy has a restoration manifest. Cancellation takes effect between files.";
        var list = new ListBox { ItemsSource = files.Select(f => f.Path + (!quarantine && DeletionPolicy.IsNetworkPath(f.Path) ? " · NETWORK - permanent deletion" : "") + "\n" + Formatting.Size(f.Size) +
            (f.IsReference ? " · PROTECTED REFERENCE" : "") + "\n" + (f.Media?.Description ?? "Media tracks not inspected yet; use Compare evidence before deciding.") + "\n" + (review?.GetValueOrDefault(f.Path) ?? "")), FontSize = 12 };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto); Grid.SetRow(list, 1); grid.Children.Add(list);
        var entry = new TextBox { Margin = new(0, 10, 0, 10), ToolTip = "Type DELETE to enable permanent deletion" };
        if (typedConfirmation)
        {
            var stack = new StackPanel { Margin = new(0, 12, 0, 0) };
            stack.Children.Add(new TextBlock { Text = discardFiles > 0 ? "Type DELETE to confirm discarding every selected copy:" : "Type DELETE to confirm permanent deletion:" }); stack.Children.Add(entry);
            Grid.SetRow(stack, 2); grid.Children.Add(stack);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
        var confirm = new Button { Content = allPermanent ? "Delete permanently" : networkFiles > 0 ? "Delete network files and recycle local files" : "Recycle local files", IsEnabled = !typedConfirmation,
            Background = new SolidColorBrush(permanent ? Color.FromRgb(150, 52, 30) : Color.FromRgb(17, 109, 116)), Foreground = Brushes.White };
        ThemeManager.Bind(confirm, Control.BackgroundProperty, typedConfirmation ? "WarningBrush" : "AccentSurface");
        if (quarantine) confirm.Content = "Move to quarantine";
        ThemeManager.Bind(confirm, Control.ForegroundProperty, typedConfirmation ? "WindowBackground" : "HeaderForeground");
        entry.TextChanged += (_, _) => confirm.IsEnabled = entry.Text == "DELETE";
        confirm.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(confirm); Grid.SetRow(buttons, 3); grid.Children.Add(buttons);
        Content = grid;
    }
}

public sealed class EvidenceWindow : Window
{
    private FileRecord reference;
    private FileRecord candidate;
    private readonly MediaProcessor media;
    private readonly CacheStore cache;
    private readonly CancellationTokenSource cancellation = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Image left = new() { Width = 450, Stretch = Stretch.Uniform };
    private readonly Image right = new() { Width = 450, Stretch = Stretch.Uniform };
    private readonly TextBlock leftDetails = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock rightDetails = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock leftPath = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8), FontSize = 12 };
    private readonly TextBlock rightPath = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8), FontSize = 12 };
    private int request;
    private CancellationTokenSource? frameRequest;
    private MatchEvidence evidence;
    private readonly ScanReport report;
    private readonly TextBlock combined = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 5) };
    public EvidenceWindow(Window owner, ResultRow row, MediaProcessor processor, CacheStore store, ScanReport? scanReport = null)
    {
        Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; Width = 1120; Height = 850; MinWidth = 900; MinHeight = 750;
        Title = "Compare evidence"; reference = row.Group.Representative; candidate = row.Member.File; media = processor; cache = store; evidence = row.Member.Evidence;
        report = scanReport ?? new ScanReport { Groups = [row.Group] };
        ThemeManager.Bind(status, TextBlock.ForegroundProperty, "MutedBrush");
        var grid = new Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = row.Group.Label, FontSize = 23, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = row.Member.Evidence.Reason, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 15) }); grid.Children.Add(heading);
        var files = row.Group.Members.Select(m => m.File).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var leftPicker = new ComboBox { ItemsSource = files.Select(f => f.Path).ToArray(), SelectedIndex = Array.FindIndex(files, f => f.Path.Equals(reference.Path, StringComparison.OrdinalIgnoreCase)) };
        var rightPicker = new ComboBox { ItemsSource = files.Select(f => f.Path).ToArray(), SelectedIndex = Array.FindIndex(files, f => f.Path.Equals(candidate.Path, StringComparison.OrdinalIgnoreCase)) };
        heading.Children.Add(leftPicker); heading.Children.Add(rightPicker); heading.Children.Add(combined);
        void UpdateCombined()
        {
            combined.Text = string.Join("\n", ReviewAnalysis.Evidence(report, reference, candidate).Select(e => e.Search + ": " + e.Reason)) +
                "\nReview decision: " + (cache.Review(reference, candidate) is { Length: > 0 } decision ? decision : "unreviewed") +
                "\n" + string.Join("\n", ReviewAnalysis.LossWarnings(candidate, [reference]));
        }
        async void PairChanged(object sender, SelectionChangedEventArgs e)
        {
            if (leftPicker.SelectedIndex < 0 || rightPicker.SelectedIndex < 0) return;
            var a = files[leftPicker.SelectedIndex]; var b = files[rightPicker.SelectedIndex];
            reference = a; candidate = b; evidence = ReferencePolicy.Compare(row.Group, a, b, report) ?? new(0, "No direct match");
            leftPath.Text = a.Path + (a.IsReference ? " [PROTECTED REFERENCE]" : ""); rightPath.Text = b.Path + (b.IsReference ? " [PROTECTED REFERENCE]" : ""); leftDetails.Text = rightDetails.Text = "Loading metadata...";
            left.Source = right.Source = null; UpdateCombined(); await LoadFrames(0); UpdateCombined();
        }
        leftPicker.SelectionChanged += PairChanged; rightPicker.SelectionChanged += PairChanged;
        var reviews = new WrapPanel();
        foreach (string decision in new[] { "Different", "Keep both", "Reviewed", "Clear decision" })
        {
            var button = new Button { Content = decision };
            button.Click += (_, _) =>
            {
                if (!reference.IsCurrent() || !candidate.IsCurrent()) { status.Text = "Files changed; rescan before recording a decision."; return; }
                cache.SaveReview(reference, candidate, decision == "Clear decision" ? "" : decision); UpdateCombined();
            };
            reviews.Children.Add(button);
        }
        heading.Children.Add(reviews); UpdateCombined();
        var pair = new Grid(); pair.ColumnDefinitions.Add(new()); pair.ColumnDefinitions.Add(new() { Width = new GridLength(18) }); pair.ColumnDefinitions.Add(new());
        StackPanel Side(string label, FileRecord file, Image image, TextBlock details)
        {
            var side = new StackPanel();
            var caption = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold };
            ThemeManager.Bind(caption, TextBlock.ForegroundProperty, "AccentBrush"); side.Children.Add(caption);
            var viewer = new ScrollViewer { Content = image, Height = 270, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            side.Children.Add(viewer);
            var pathText = ReferenceEquals(image, left) ? leftPath : rightPath; pathText.Text = file.Path + (file.IsReference ? " [PROTECTED REFERENCE]" : ""); side.Children.Add(pathText);
            side.Children.Add(details);
            var open = new Button { Content = "Play this video", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 8, 0, 0) };
            open.Click += (_, _) => { try { var current = ReferenceEquals(image, left) ? reference : candidate; System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(current.Path) { UseShellExecute = true }); } catch (Exception ex) { status.Text = ex.Message; } };
            side.Children.Add(open); return side;
        }
        pair.Children.Add(Side("LEFT FILE", reference, left, leftDetails)); var candidateSide = Side("RIGHT FILE", candidate, right, rightDetails);
        Grid.SetColumn(candidateSide, 2); pair.Children.Add(candidateSide);
        var pairScroll = new ScrollViewer { Content = pair, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(pairScroll, 1); grid.Children.Add(pairScroll);
        var positions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 12, 0, 10) };
        positions.Children.Add(new TextBlock { Text = "Sample position", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) });
        var combo = new ComboBox { ItemsSource = Enumerable.Range(0, 12).Select(i => $"Frame {i + 1} · {MediaProcessor.Position(i):P0}"), Width = 180, SelectedIndex = 0 };
        combo.SelectionChanged += async (_, _) => await LoadFrames(combo.SelectedIndex); positions.Children.Add(combo); Grid.SetRow(positions, 2); grid.Children.Add(positions);
        positions.Children.Add(new TextBlock { Text = "Zoom", Margin = new(20, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        var zoom = new Slider { Minimum = 1, Maximum = 3, Value = 1, Width = 150, VerticalAlignment = VerticalAlignment.Center };
        zoom.ValueChanged += (_, _) => { left.Width = right.Width = 450 * zoom.Value; }; positions.Children.Add(zoom);
        var timeline = new Slider { Minimum = 0, Maximum = 100, Value = 10, Width = 130, Margin = new(12, 0, 5, 0), ToolTip = "Arbitrary synchronized timeline position (%)" };
        positions.Children.Add(timeline);
        var percentage = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 6, 0) };
        percentage.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Value") { Source = timeline, StringFormat = "{0:0.#}%" }); positions.Children.Add(percentage);
        var seek = new Button { Content = "Show %" }; seek.Click += async (_, _) => { await LoadFrames(-1, timeline.Value / 100); UpdateCombined(); }; positions.Children.Add(seek);
        Grid.SetRow(status, 3); grid.Children.Add(status); Content = grid;
        Loaded += async (_, _) => { await LoadFrames(0); UpdateCombined(); };
        Closed += (_, _) => { cancellation.Cancel(); frameRequest?.Cancel(); };
    }
    public async Task LoadFrames(int index, double? fraction = null)
    {
        if (fraction == null && (index is < 0 or > 11) || cancellation.IsCancellationRequested) return;
        double position = fraction ?? MediaProcessor.Position(index);
        var reference = this.reference; var candidate = this.candidate;
        frameRequest?.Cancel();
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token); frameRequest = pending;
        var token = pending.Token;
        int id = ++request; status.Text = "Reading colour comparison frames…";
        try
        {
            var result = await Task.Run(async () =>
            {
                if (!reference.IsCurrent() || !candidate.IsCurrent()) throw new IOException("A file changed or is unavailable. Run a new scan.");
                reference.Media ??= await media.ProbeAsync(reference.Path, token);
                if (!ReferenceEquals(reference, candidate)) candidate.Media ??= await media.ProbeAsync(candidate.Path, token);
                else candidate.Media = reference.Media;
                var a = await media.PreviewAsync(reference.Path, Math.Min(reference.Media.Duration * position, Math.Max(0, reference.Media.Duration - .1)), token);
                var b = reference.Path.Equals(candidate.Path, StringComparison.OrdinalIgnoreCase) ? a
                    : await media.PreviewAsync(candidate.Path, Math.Min(candidate.Media!.Duration * position, Math.Max(0, candidate.Media.Duration - .1)), token);
                if (!reference.IsCurrent() || !candidate.IsCurrent()) throw new IOException("A file changed while reading comparison frames.");
                token.ThrowIfCancellationRequested(); cache.SaveMany([reference, candidate]); return (a, b);
            }, token);
            if (id != request || cancellation.IsCancellationRequested) return;
            left.Source = ImageFrom(result.a); right.Source = ImageFrom(result.b);
            leftDetails.Text = Details(reference); rightDetails.Text = Details(candidate);
            string distance = index >= 0 && evidence.FrameDistances is { Count: 12 } distances ? $"Fingerprint distance: {distances[index]}/64. " : "";
            status.Text = $"Position {position:P1} · {distance}Review colour, detail, audio and subtitle tracks before choosing a copy.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (id == request) status.Text = ex.Message; }
        finally { if (ReferenceEquals(frameRequest, pending)) frameRequest = null; }
    }
    private static BitmapSource ImageFrom(byte[] pixels)
    {
        using var stream = new MemoryStream(pixels);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
    private static string Details(FileRecord file) => file.Media is { } m
        ? $"{Formatting.Size(file.Size)} · {Formatting.Duration(m.Duration)}\n{m.Description}"
        : Formatting.Size(file.Size);
}
