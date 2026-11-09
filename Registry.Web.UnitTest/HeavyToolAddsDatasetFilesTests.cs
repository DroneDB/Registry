using System;
using System.Linq;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.HeavyTasks.Tools;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The catalog of the heavy tools that add files to a dataset (FR-XC-26): only these record what they create in the
/// write record of their task and are rolled back from it when the task is cancelled, fails or is lost (FR-XC-27).
/// </summary>
[TestFixture]
public class HeavyToolAddsDatasetFilesTests
{
    // The flag is a constant of each tool: no instance (and none of its services) is needed to read it
    private static IHeavyTool Uninitialized(Type type) => (IHeavyTool)RuntimeHelpers.GetUninitializedObject(type);

    private static Type[] AllTools() =>
    [
        .. typeof(IHeavyTool).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IHeavyTool).IsAssignableFrom(t))
    ];

    /// <remarks>
    /// The mask-borders step is not here: it is a DdbJobs job, not a heavy tool, and its unit (U-306, FR-PRC-12) sets the
    /// flag when it becomes one.
    /// </remarks>
    [Test]
    public void ExactlyThePhotogrammetryTheImportsTheExtractionMergeAndAlignAddDatasetFiles()
    {
        var adding = AllTools().Where(t => Uninitialized(t).AddsDatasetFiles).Select(t => t.Name).Order().ToArray();

        adding.ShouldBe(
        [
            nameof(AlignRasterTool), nameof(ArchiveExtractTool), nameof(FileUrlImportTool), nameof(ImportDatasetTool),
            nameof(MergeMultispectralTool), nameof(PhotogrammetryTool)
        ]);
    }

    [Test]
    public void ATool_ByDefault_AddsNoDatasetFiles()
    {
        var others = AllTools().Where(t => !Uninitialized(t).AddsDatasetFiles).ToArray();

        others.ShouldNotBeEmpty();
        others.ShouldContain(typeof(BuildTool));
        others.ShouldContain(typeof(RasterExportTool));
        others.ShouldContain(typeof(RescanIndexTool));
    }
}
