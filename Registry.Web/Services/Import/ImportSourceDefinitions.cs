#nullable enable
using System;
using System.Collections.Generic;

namespace Registry.Web.Services.Import;

/// <summary>
/// Per-source-type definitions shared between the encrypt side (<see cref="Registry.Web.Services.Managers.ImportManager"/>)
/// and the decrypt side (<see cref="Registry.Web.Services.HeavyTasks.Tools.ImportDatasetTool"/>).
/// Centralizes the contract so that adding a new source type only requires one change.
/// </summary>
internal static class ImportSourceDefinitions
{
    /// <summary>Per-source-type fields that must be encrypted at rest before being handed to the worker.</summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> SensitiveFields =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["registry"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "password" },
            ["archive-url"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "password" }
        };
}
