using SimilarVideoFinder.Core;
using System.Windows;
using System.Windows.Controls;
namespace SimilarVideoFinder.App;
public sealed class SelectionPreviewWindow : Window
{
    public SelectionPreviewWindow(Window owner, ScanReport report, IReadOnlySet<string> selected)
    {
        Owner = owner; Title = "Preview keep rules"; Width = 850; Height = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new Thickness(24) }; grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = $"Select {selected.Count:N0} files for deletion?", FontSize = 24, Margin = new(0, 0, 0, 12) });
        var files = report.Groups.SelectMany(g => g.Members).Select(m => m.File).Where(f => selected.Contains(f.Path)).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var list = new ListBox { ItemsSource = files.Select(f =>
        {
            var retained = ReviewAnalysis.Retained(report, f, selected);
            return "SELECT: " + f.Path + "\nKEEP: " + string.Join("\nKEEP: ", retained.Select(r => r.Path)) + "\n" + string.Join("\n", ReviewAnalysis.LossWarnings(f, retained));
        }) };
        Grid.SetRow(list, 1); grid.Children.Add(list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true }); var apply = new Button { Content = "Apply selection" }; apply.Click += (_, _) => DialogResult = true; buttons.Children.Add(apply); Grid.SetRow(buttons, 2); grid.Children.Add(buttons); Content = grid;
    }
}
