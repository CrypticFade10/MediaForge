using System.Windows;

namespace MediaForge.Tooling;

/// <summary>A tool compiled into the suite. Construct windows only when requested.</summary>
public interface IToolModule
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    string Category { get; }
    Window CreateWindow(ToolContext context);
}

/// <summary>Per-tool storage and command-line arguments supplied by the host.</summary>
public sealed record ToolContext(string DataDirectory, IReadOnlyList<string> Arguments);

public sealed class ToolRegistry
{
    private readonly IReadOnlyList<IToolModule> modules;
    public IReadOnlyList<IToolModule> Modules => modules;

    public ToolRegistry(IEnumerable<IToolModule> tools)
    {
        var items = tools.ToArray();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in items)
        {
            ArgumentNullException.ThrowIfNull(tool);
            if (string.IsNullOrWhiteSpace(tool.Id) || tool.Id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
                throw new ArgumentException("Tool IDs must contain only ASCII letters, digits and hyphens.");
            if (!ids.Add(tool.Id)) throw new ArgumentException($"Duplicate tool ID: {tool.Id}");
            if (string.IsNullOrWhiteSpace(tool.Name)) throw new ArgumentException("Each tool needs a display name.");
        }
        modules = Array.AsReadOnly(items);
    }

    public IToolModule? Find(string id) => modules.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}
