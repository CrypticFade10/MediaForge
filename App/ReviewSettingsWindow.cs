using SimilarVideoFinder.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace SimilarVideoFinder.App;

public sealed class ReviewSettingsWindow : Window
{
    public ReviewSettingsWindow(Window owner, ScanOptions options, KeepPreferences preferences, Action<ScanOptions, KeepPreferences> save)
    {
        Owner = owner; Title = "Review and scan preferences"; Width = 620; Height = 680; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        TextBox Field(string label, string value) { panel.Children.Add(new TextBlock { Text = label, Margin = new(0, 10, 0, 4) }); var box = new TextBox { Text = value }; panel.Children.Add(box); return box; }
        var folders = Field("Excluded folders (one absolute path per line)", string.Join(Environment.NewLine, options.ExcludedFolders)); folders.AcceptsReturn = true; folders.Height = 65;
        var patterns = Field("Excluded filename patterns, separated by ; (for example *sample*;*trailer*)", options.ExcludedPatterns);
        var size = Field("Minimum file size (MiB)", (options.MinimumSizeBytes / 1048576d).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var duration = Field("Minimum duration (seconds; 0 disables metadata reads for this filter)", options.MinimumDurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var folder = Field("Preferred keep folder", preferences.Folder);
        var codec = Field("Preferred video codec (for example hevc, h264, av1)", preferences.Codec);
        var hdr = Field("Preferred dynamic range text (for example HDR, SDR, Dolby Vision)", preferences.DynamicRange);
        var language = Field("Preferred audio language code (for example eng)", preferences.AudioLanguage);
        var button = new Button { Content = "Save preferences", Margin = new(0, 20, 0, 0) };
        button.Click += (_, _) =>
        {
            try
            {
                options.ExcludedFolders = folders.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Path.GetFullPath).ToList();
                options.ExcludedPatterns = patterns.Text;
                options.MinimumSizeBytes = checked((long)(double.Parse(size.Text, System.Globalization.CultureInfo.InvariantCulture) * 1048576));
                options.MinimumDurationSeconds = double.Parse(duration.Text, System.Globalization.CultureInfo.InvariantCulture);
                if (options.MinimumSizeBytes < 0 || !double.IsFinite(options.MinimumDurationSeconds) || options.MinimumDurationSeconds < 0) throw new ArgumentException("Use non-negative size and duration.");
                if (!string.IsNullOrWhiteSpace(folder.Text)) _ = Path.GetFullPath(folder.Text);
                if (string.IsNullOrWhiteSpace(codec.Text) || string.IsNullOrWhiteSpace(hdr.Text) || string.IsNullOrWhiteSpace(language.Text)) throw new ArgumentException("Enter codec, dynamic range and language preferences.");
                save(options, new(folder.Text.Trim(), codec.Text.Trim(), hdr.Text.Trim(), language.Text.Trim())); DialogResult = true;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Check preferences"); }
        };
        panel.Children.Add(button); Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
