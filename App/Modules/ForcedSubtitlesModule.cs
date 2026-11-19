using MediaForge.Tooling;
using System.Windows;

namespace SimilarVideoFinder.App.Modules;

public sealed class ForcedSubtitlesModule : IToolModule
{
    public string Id => "forced-subtitles";
    public string Name => "Forced Subtitle Generator";
    public string Description => "Detect foreign-language speech in English audio and translate it into an English forced SRT.";
    public string Category => "Subtitles";
    public Window CreateWindow(ToolContext context) => new Subtitles.ForcedSubtitlesWindow(context);
}
