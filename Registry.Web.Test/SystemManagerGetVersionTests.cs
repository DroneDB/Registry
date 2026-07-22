#nullable enable
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

    private SystemManager CreateSystemManager()
    {
        var authManagerMock = new Mock<IAuthManager>();
        var appSettingsMock = new Mock<IOptions<AppSettings>>();
        appSettingsMock.Setup(o => o.Value).Returns(new AppSettings
        {
            DatasetsPath = "test-datasets"
        });

        var backgroundJobMock = new Mock<IBackgroundJobsProcessor>();
        var cacheManagerMock = new Mock<ICacheManager>();
        var httpClientFactoryMock = new Mock<IHttpClientFactory>();

        await using var context = new RegistryContext(
            new DbContextOptionsBuilder<RegistryContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options);

        var ddbManagerMock = new Mock<IDdbManager>();
        var objectsManagerMock = new Mock<IObjectsManager>();

        var buildPendingService = new BuildPendingService(
            context,
            ddbManagerMock.Object,
            backgroundJobMock.Object,
            cacheManagerMock.Object,
            Mock.Of<ILogger<BuildPendingService>>());

        return new SystemManager(
            authManagerMock.Object,
            context,
            ddbManagerMock.Object,
            _logger,
            objectsManagerMock.Object,
            appSettingsMock.Object,
            buildPendingService,
            httpClientFactoryMock.Object,
            backgroundJobMock.Object,
            cacheManagerMock.Object,
            _fileSystem,
            Mock.Of<IJobIndexWriter>(),
            new SsrfGuard(new ImportSettings())
        );
    }

    [Test]
    public void GetVersion_ReturnsThreePartSemver()
    {
        var systemManager = CreateSystemManager();

        var version = systemManager.GetVersion();

        version.ShouldNotBeNullOrEmpty("GetVersion should return a non-empty version string");

        // Assert major.minor.patch (3 components) and reject 4-part versions like "2.5.4.0"
        var parts = version.Split('.');
        parts.Length.ShouldBe(3,
            $"Expected 3-part semver but got {parts.Length} parts: '{version}'");

        for (var i = 0; i < parts.Length; i++)
        {
            int.TryParse(parts[i]).ShouldBeTrue(
                $"Part at index {i} ('{parts[i]}') should be a valid integer");
        }
    }
}