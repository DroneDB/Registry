#nullable enable
using System.Collections.Generic;
using Registry.Web.Services.HeavyTasks.Ports;

namespace Registry.Web.Services.HeavyTasks.Ports;

/// <summary>
/// In-process catalog of available heavy tools (native, registered via DI).
/// Remote tools (exposed by Processing Nodes) are aggregated separately in a
/// later sprint; this registry only holds in-process <see cref="IHeavyTool"/>s.
/// </summary>
public interface IHeavyToolRegistry
{
    /// <summary>All registered native tools.</summary>
    IReadOnlyCollection<IHeavyTool> All { get; }

    /// <summary>
    /// Resolves a tool by id and (optionally) version. When <paramref name="version"/>
    /// is null the highest registered version wins. Returns null when not found.
    /// </summary>
    IHeavyTool? Resolve(string toolId, string? version = null);
}
