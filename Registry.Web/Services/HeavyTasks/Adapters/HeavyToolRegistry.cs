#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Registry.Web.Services.HeavyTasks.Ports;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// Immutable in-memory registry of native heavy tools. Populated from the set of
/// <see cref="IHeavyTool"/> instances registered in DI. Resolution by id picks the
/// highest version when none is requested.
/// </summary>
public sealed class HeavyToolRegistry : IHeavyToolRegistry
{
    // toolId -> (version -> tool)
    private readonly Dictionary<string, Dictionary<string, IHeavyTool>> _byId =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<IHeavyTool> _all = new();

    public HeavyToolRegistry(IEnumerable<IHeavyTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Id))
                throw new InvalidOperationException("A heavy tool with an empty Id was registered.");
            if (string.IsNullOrWhiteSpace(tool.Version))
                throw new InvalidOperationException($"Tool '{tool.Id}' has an empty Version.");

            if (!_byId.TryGetValue(tool.Id, out var versions))
            {
                versions = new Dictionary<string, IHeavyTool>(StringComparer.OrdinalIgnoreCase);
                _byId[tool.Id] = versions;
            }

            if (!versions.TryAdd(tool.Version, tool))
                throw new InvalidOperationException(
                    $"Duplicate registration for tool '{tool.Id}' version '{tool.Version}'.");

            _all.Add(tool);
        }
    }

    public IReadOnlyCollection<IHeavyTool> All => _all;

    public IHeavyTool? Resolve(string toolId, string? version = null)
    {
        if (string.IsNullOrWhiteSpace(toolId) || !_byId.TryGetValue(toolId, out var versions))
            return null;

        if (!string.IsNullOrWhiteSpace(version))
            return versions.TryGetValue(version, out var exact) ? exact : null;

        // Highest version (numeric when possible, else ordinal).
        return versions
            .OrderByDescending(kv => int.TryParse(kv.Key, out var n) ? n : int.MinValue)
            .ThenByDescending(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .First().Value;
    }
}
