using MediaForge.Tooling;
using System.Windows;

namespace SimilarVideoFinder.App.Modules;

public sealed class IntegrityCheckerModule : IToolModule
{
    public string Id => "media-integrity";
    public string Name => "Media Integrity Checker";
    public string Description => "Recursively check video and audio files for decoding errors. Inspect containers or run a full CPU/GPU decode, then export the results.";
    public string Category => "Media health";
    public Window CreateWindow(ToolContext context) => new Integrity.IntegrityWindow(context);
}
