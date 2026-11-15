using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SimilarVideoFinder.App;

internal static class Branding
{
    public static void Apply()
    {
        using var artwork = Assembly.GetExecutingAssembly().GetManifestResourceStream("Branding.MediaForge.png");
        if (artwork == null) return;
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = artwork; image.EndInit(); image.Freeze();
        var current = Application.Current.Resources[typeof(Window)] as Style;
        var style = new Style(typeof(Window), current);
        style.Setters.Add(new Setter(Window.IconProperty, image));
        Application.Current.Resources[typeof(Window)] = style;
    }
}
