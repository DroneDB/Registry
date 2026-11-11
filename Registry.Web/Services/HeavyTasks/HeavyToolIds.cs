namespace Registry.Web.Services.HeavyTasks;

/// <summary>
/// Canonical identifiers of the built-in heavy tools, shared by every component that
/// has to recognize a task by its tool rather than by a local string literal.
/// </summary>
public static class HeavyToolIds
{
    /// <summary>Dataset build tool (matches the JobIndex.ToolId default).</summary>
    public const string Build = "build";

    /// <summary>Index rescan tool.</summary>
    public const string RescanIndex = "rescan-index";

    /// <summary>Dataset import tool: <c>ImportManager</c> creates the dataset it fills.</summary>
    public const string ImportDataset = "import-dataset";

    /// <summary>Mask Borders: the masked copy of a georaster (FR-PRC-12, D-013).</summary>
    public const string MaskBorders = "mask-borders";
}
