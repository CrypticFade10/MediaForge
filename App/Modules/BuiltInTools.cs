using MediaForge.Tooling;

namespace SimilarVideoFinder.App.Modules;

/// <summary>Add built-in modules here; the launcher needs no tool-specific changes.</summary>
public static class BuiltInTools
{
    public static ToolRegistry CreateRegistry() => new([new DuplicateFinderModule(), new IntegrityCheckerModule()]);
}
