#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Models.DTO;
using Registry.Web.Services.RemoteImport;
using Shouldly;
using Registry.Web.Test.Support;

namespace Registry.Web.Test;

/// <summary>
/// The remote Registry import (<see cref="RemoteRegistryImporter"/>, the import job) against a real remote registry.
/// These tests require network access and should be run explicitly.
/// </summary>
[TestFixture]
[Category(TestCategories.Network)]
[Explicit("These tests require network access and download real data from hub.dronedb.app")]
public class RemoteRegistryImporterNetworkTest : TestBase
{
    private const string TestRegistryUrl = "https://hub.dronedb.app";
    private const string TestOrganization = "odm";
    private const string TestDataset = "brighton-beach";

    private string _tempDatasetsPath = null!;
    private string _tempPath = null!;

    [SetUp]
    public void Setup()
    {
        // Create temp directories for test
        _tempDatasetsPath = Path.Combine(Path.GetTempPath(), $"registry-test-{Guid.NewGuid()}");
        _tempPath = Path.Combine(Path.GetTempPath(), $"registry-test-temp-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDatasetsPath);
        Directory.CreateDirectory(_tempPath);
    }

    [TearDown]
    public void TearDown()
    {
        // Cleanup temp directories
        try
        {
            if (Directory.Exists(_tempDatasetsPath))
                Directory.Delete(_tempDatasetsPath, recursive: true);
            if (Directory.Exists(_tempPath))
                Directory.Delete(_tempPath, recursive: true);
        }
        catch (Exception ex)
        {
            TestContext.Out.WriteLine($"Warning: Failed to cleanup temp directories: {ex.Message}");
        }
    }

    private static RegistryContext CreateInMemoryContext()
    {
        return TestDb.CreateContext<RegistryContext>();
    }

    private RemoteRegistryImporter CreateImporter(RegistryContext context, Mock<IDdbManager> ddbManagerMock) =>
        TestImports.Importer(context, ddbManagerMock.WithFolders(_tempDatasetsPath).Object, new TestHttpClientFactory(),
            _tempPath);

    private static RemoteImportRequest Request(string destinationOrg, string destinationDs, ImportMode mode) =>
        new(TestRegistryUrl, null, null, TestOrganization, TestDataset, destinationOrg, destinationDs, mode);

    [Test]
    public async Task ImportDataset_ArchiveMode_DownloadsAndImportsPublicDataset()
    {
        // Arrange
        await using var context = CreateInMemoryContext();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.DatasetFolderPath).Returns(() =>
            Path.Combine(_tempDatasetsPath, "imported-org", context.Datasets.First().InternalRef.ToString()));

        var ddbManagerMock = new Mock<IDdbManager>();
        ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var importer = CreateImporter(context, ddbManagerMock);

        var request = Request("imported-org", "imported-dataset", ImportMode.Archive);

        // Act
        TestContext.Out.WriteLine($"Starting Archive mode import of {TestOrganization}/{TestDataset}...");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await importer.ImportAsync(request, null, CancellationToken.None);

        stopwatch.Stop();
        TestContext.Out.WriteLine($"Import completed in {stopwatch.Elapsed.TotalSeconds:F2} seconds");

        // Assert
        result.ShouldNotBeNull();
        result.Errors.ShouldBeEmpty($"Import should succeed without errors. Errors: {string.Join(", ", result.Errors.Select(e => e.Message))}");
        result.ImportedItems.Length.ShouldBe(1);
        result.ImportedItems[0].Organization.ShouldBe("imported-org");
        result.ImportedItems[0].Dataset.ShouldBe("imported-dataset");
        result.TotalFiles.ShouldBeGreaterThan(0);
        result.TotalSize.ShouldBeGreaterThan(0);

        TestContext.Out.WriteLine($"Imported {result.TotalFiles} files, total size: {result.TotalSize / 1024.0 / 1024.0:F2} MB");
        TestContext.Out.WriteLine($"Duration: {result.Duration.TotalSeconds:F2} seconds");
    }

    [Test]
    public async Task ImportDataset_ParallelFilesMode_DownloadsAndImportsPublicDataset()
    {
        // Arrange
        await using var context = CreateInMemoryContext();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.DatasetFolderPath).Returns(() =>
            Path.Combine(_tempDatasetsPath, "imported-org-parallel", context.Datasets.First().InternalRef.ToString()));
        ddbMock.Setup(x => x.FindSummaries(It.IsAny<EntryQuery>())).Returns([]); // No existing files
        ddbMock.Setup(x => x.AddRaw(It.IsAny<string>())); // Accept any file addition

        var ddbManagerMock = new Mock<IDdbManager>();
        ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var importer = CreateImporter(context, ddbManagerMock);

        var request = Request("imported-org-parallel", "imported-dataset-parallel", ImportMode.ParallelFiles);

        // Act
        TestContext.Out.WriteLine($"Starting ParallelFiles mode import of {TestOrganization}/{TestDataset}...");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await importer.ImportAsync(request, null, CancellationToken.None);

        stopwatch.Stop();
        TestContext.Out.WriteLine($"Import completed in {stopwatch.Elapsed.TotalSeconds:F2} seconds");

        // Assert
        result.ShouldNotBeNull();
        result.Errors.ShouldBeEmpty($"Import should succeed without errors. Errors: {string.Join(", ", result.Errors.Select(e => e.Message))}");
        result.FileErrors.ShouldBeEmpty($"File imports should succeed. File errors: {string.Join(", ", result.FileErrors?.Select(e => $"{e.FilePath}: {e.Message}") ?? [])}");
        result.ImportedItems.Length.ShouldBe(1);
        result.ImportedItems[0].Organization.ShouldBe("imported-org-parallel");
        result.ImportedItems[0].Dataset.ShouldBe("imported-dataset-parallel");
        result.TotalFiles.ShouldBeGreaterThan(0);
        result.TotalSize.ShouldBeGreaterThan(0);

        // Verify AddRaw was called for each file
        ddbMock.Verify(x => x.AddRaw(It.IsAny<string>()), Times.AtLeastOnce());

        TestContext.Out.WriteLine($"Imported {result.TotalFiles} files, skipped {result.SkippedFiles} files");
        TestContext.Out.WriteLine($"Total size: {result.TotalSize / 1024.0 / 1024.0:F2} MB");
        TestContext.Out.WriteLine($"Duration: {result.Duration.TotalSeconds:F2} seconds");

        if (result.FileErrors?.Length > 0)
        {
            TestContext.Out.WriteLine($"File errors ({result.FileErrors.Length}):");
            foreach (var error in result.FileErrors)
            {
                TestContext.Out.WriteLine($"  - {error.FilePath}: {error.Message} (retries: {error.RetryCount})");
            }
        }
    }
}