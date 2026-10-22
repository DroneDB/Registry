#nullable enable
using System;
using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Registry.Adapters;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Builds;
using Registry.Web.Services.Import;
using Registry.Web.Services.RemoteImport;

namespace Registry.Web.Test.Support;

/// <summary>The remote Registry import (RQ-12) as the job host builds it, over test doubles.</summary>
public static class TestImports
{
    /// <summary>
    /// <see cref="RemoteRegistryImporter"/> with both strategies, its HTTP calls through <paramref name="http"/>.
    /// </summary>
    public static RemoteRegistryImporter Importer(RegistryContext context, IDdbManager ddbManager,
        IHttpClientFactory http, string tempPath, IBuildScheduler? builds = null, ImportSettings? settings = null)
    {
        settings ??= new ImportSettings();
        var fileSystem = new FileSystem();
        var remote = new RemoteRegistryClient(http, settings, TestPaths.Policy,
            NullLogger<RemoteRegistryClient>.Instance);
        var destinations = new ImportDestinations(context, ddbManager, fileSystem, builds ?? TestJobs.Scheduler(),
            TimeProvider.System, NullLogger<ImportDestinations>.Instance);

        return new RemoteRegistryImporter(remote,
        [
            new ArchiveImportStrategy(remote, destinations, fileSystem,
                Microsoft.Extensions.Options.Options.Create(new StorageOptions { TempPath = tempPath }),
                NullLogger<ArchiveImportStrategy>.Instance),
            new ParallelFilesImportStrategy(remote, destinations, fileSystem, TestPaths.Policy, settings,
                NullLogger<ParallelFilesImportStrategy>.Instance)
        ], TimeProvider.System, NullLogger<RemoteRegistryImporter>.Instance);
    }

    /// <summary>Puts the dataset folders of <paramref name="ddbManager"/> under <paramref name="datasetsPath"/>.</summary>
    public static Mock<IDdbManager> WithFolders(this Mock<IDdbManager> ddbManager, string datasetsPath)
    {
        ddbManager.Setup(m => m.GetDatasetFolder(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns((string org, Guid internalRef) => Path.Combine(datasetsPath, org, internalRef.ToString()));
        return ddbManager;
    }
}
