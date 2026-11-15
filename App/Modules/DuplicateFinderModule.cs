using MediaForge.Tooling;
using System.Windows;

namespace SimilarVideoFinder.App.Modules;

public sealed class DuplicateFinderModule : IToolModule
{
    public string Id => "duplicate-finder";
    public string Name => "Duplicate Finder";
    public string Description => "Find identical files and similar videos. Compare evidence, choose copies to keep, and safely delete or quarantine unwanted files.";
    public string Category => "Library management";
    public Window CreateWindow(ToolContext context) => new MainWindow(context.Arguments.ToArray());
}
