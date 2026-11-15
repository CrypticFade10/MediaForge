# Adding a tool to MediaForge

The launcher discovers tools through the explicitly registered `IToolModule` contract in `Tooling/MediaForge.Tooling.csproj`. It contains no matching logic or dependency on Duplicate Finder. The existing Core project contains the duplicate matching engine; the App project hosts the launcher, the current module and shared application resources.

## Module contract

Implement `MediaForge.Tooling.IToolModule` in a new class under `App/Modules`, or in a separate WPF class-library project that references Tooling. For a separate project, add a project reference from App to it. Tool implementations must not reference the App executable; use the supplied context and WPF dynamic resources instead.

```csharp
using MediaForge.Tooling;
using System.Windows;

public sealed class MediaInspectorModule : IToolModule
{
    public string Id => "media-inspector";
    public string Name => "Media Inspector";
    public string Description => "Inspect media metadata.";
    public string Category => "Media analysis";
    public Window CreateWindow(ToolContext context) => new InspectorWindow(context);
}
```

The example describes an extension point; Media Inspector is not bundled yet.

Register the new module in `App/Modules/BuiltInTools.cs`. Run `./build.ps1` to validate and republish. The launcher displays registered modules automatically and supports searching their names, descriptions and categories. Module IDs must be unique, case-insensitively, and contain only ASCII letters, digits and hyphens.

## Lifecycle and storage

Create expensive services lazily when your window loads. `CreateWindow` runs on the UI thread and should return promptly. The host keeps one open window per tool and activates it on repeat launches. Closing a tool releases its slot. Dispose resources on close, and implement cancellable close handling for operations that must finish or stop safely. A failed window factory can be retried.

The host passes `ToolContext.DataDirectory`, scoped to `modules/<tool-id>` below the selected data directory. Duplicate Finder alone uses the legacy root to preserve existing data. Use only the supplied module directory for tool-specific settings. Saving media information should be an explicit user choice, consistent with Duplicate Finder's history-off default. Arguments are available through a read-only list.

Use shared dynamic resources such as `Surface`, `TextBrush`, `MutedBrush`, `BorderBrush` and `AccentBrush` for theme support. Application-wide theme resources update open windows together.

Modules are built into the EXE. External DLL loading and separately installed plugins are deliberately outside this distribution model. The suite starts offline and does not contact GitHub or upload scanned media.

## Validation

Run `MediaForge.exe --suite-smoke --data-dir <empty-test-folder>` to check registration validation, searching, theme rendering, opening/reusing/reopening Duplicate Finder, media-tool preparation, module storage isolation and recovery from factory errors. The command exits with a failing status on any failed check and writes `suite-smoke.json` plus light/dark screenshots.

Duplicate Finder's existing `--smoke` checks and both Core regression suites remain available. Test a newly added module's own behavior before shipping it.
