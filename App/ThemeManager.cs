using System.Windows;
using System.Windows.Media;

namespace SimilarVideoFinder.App;

public static class ThemeManager
{
    public static void Apply(bool dark)
    {
        var palette = dark ? new Dictionary<string, string>
        {
            ["WindowBackground"] = "#151D24", ["Surface"] = "#202B34", ["SurfaceAlt"] = "#293741", ["BorderBrush"] = "#435561",
            ["TextBrush"] = "#E9F0F3", ["MutedBrush"] = "#B1C2CA", ["AccentBrush"] = "#69CDD2", ["AccentSurface"] = "#205760",
            ["HeaderBackground"] = "#101820", ["HeaderForeground"] = "#F2F7FA", ["HeaderMuted"] = "#B1CED8",
            ["WarningSurface"] = "#493025", ["WarningBrush"] = "#FFBE9B", ["SelectionBrush"] = "#365E70"
        } : new Dictionary<string, string>
        {
            ["WindowBackground"] = "#F5F8F8", ["Surface"] = "#FFFFFF", ["SurfaceAlt"] = "#EDF3F4", ["BorderBrush"] = "#CEDCDF",
            ["TextBrush"] = "#18333D", ["MutedBrush"] = "#647D87", ["AccentBrush"] = "#116D74", ["AccentSurface"] = "#116D74",
            ["HeaderBackground"] = "#153440", ["HeaderForeground"] = "#FFFFFF", ["HeaderMuted"] = "#C5D8DE",
            ["WarningSurface"] = "#FFF1EA", ["WarningBrush"] = "#923E1E", ["SelectionBrush"] = "#C5E4E8"
        };
        foreach (var (key, color) in palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze();
            Application.Current.Resources[key] = brush;
        }
        foreach (var (key, name) in new (ResourceKey, string)[]
        {
            (SystemColors.WindowBrushKey, "Surface"), (SystemColors.WindowTextBrushKey, "TextBrush"),
            (SystemColors.ControlBrushKey, "SurfaceAlt"), (SystemColors.ControlTextBrushKey, "TextBrush"),
            (SystemColors.ControlDarkBrushKey, "BorderBrush"), (SystemColors.GrayTextBrushKey, "MutedBrush"),
            (SystemColors.HighlightBrushKey, "SelectionBrush"), (SystemColors.HighlightTextBrushKey, "TextBrush"),
            (SystemColors.InactiveSelectionHighlightBrushKey, "SurfaceAlt"), (SystemColors.InactiveSelectionHighlightTextBrushKey, "TextBrush")
        }) Application.Current.Resources[key] = Application.Current.Resources[name];
    }
    public static void Bind(FrameworkElement element, DependencyProperty property, string resource) => element.SetResourceReference(property, resource);
}
