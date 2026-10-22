#nullable enable
using Registry.Web.Services.Jobs;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shouldly;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Import;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using System.Net.Http;
using Hangfire;
using Registry.Web.Test.Support;
using Registry.Web.Services.Caching;

// ──────────────────────────────────────────────────────────────────────
// Add a unit test that asserts GetVersion() always returns major.minor.patch
// (3-part semver) and never a 4-part string ending in ".0" or similar.
// ──────────────────────────────────────────────────────────────────────

// The test class is in a separate namespace from the original regression
// surface area (cleanup/import/rescan) to keep the change localized.

namespace Registry.Web.Test.VersionTests;

[TestFixture]
public class SystemManagerGetVersionTests : TestBase
{
    private ILogger<SystemManager> _logger = null!;
    private IFileSystem _fileSystem = null!;

    [SetUp]
    public void Setup()
    {
        _logger = CreateTestLogger<SystemManager>();
        _fileSystem = new FileSystem();
    }

    private async Task<SystemManager> CreateSystemManager()
    {
        var authManagerMock = new Mock<IAuthManager>();
        var settingsOptions = TestSettings.Options(new AppSettings
        {
            DatasetsPath = "test-datasets"
        });

        var backgroundJobMock = new Mock<IBackgroundJobsProcessor>();
        var cacheManagerMock = new Mock<ICacheManager>();
        var httpClientFactoryMock = new Mock<IHttpClientFactory>();

        await using var context = TestDb.CreateContext<RegistryContext>();

        var ddbManagerMock = new Mock<IDdbManager>();
        var objectsManagerMock = new Mock<IObjectsManager>();

        var buildPendingService = new BuildPendingService(context, new DatasetSweep(context), ddbManagerMock.Object, backgroundJobMock.Object, cacheManagerMock.Object, TestJobs.NewStorage(), Mock.Of<ILogger<BuildPendingService>>());

        return new SystemManager(
            authManagerMock.Object,
            context,
            ddbManagerMock.Object,
            _logger,
            objectsManagerMock.Object,
            settingsOptions,
            buildPendingService,
            httpClientFactoryMock.Object,
            backgroundJobMock.Object,
            Mock.Of<IDatasetListingInvalidator>(),
            _fileSystem,
            Mock.Of<IJobIndexWriter>(),
            new SsrfGuard(new ImportSettings())
        , TestPaths.Policy, TimeProvider.System, TestJobs.Scheduler());
    }

    [Test]
    public async Task GetVersion_ReturnsThreePartSemver()
    {
        var systemManager = await CreateSystemManager();

        var version = systemManager.GetVersion();

        version.ShouldNotBeNullOrEmpty("GetVersion should return a non-empty version string");

        // Assert major.minor.patch (3 components) and reject 4-part versions like "2.5.4.0"
        var parts = version.Split('.');
        parts.Length.ShouldBe(3,
            $"Expected 3-part semver but got {parts.Length} parts: '{version}'");

        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var val))
                Assert.Fail($"Expected part {i} to be integer but got {parts[i]}");
        }
    }
}