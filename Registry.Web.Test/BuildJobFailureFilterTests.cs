#nullable enable
using System;
using System.Threading;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Web.Filters;
using Registry.Web.Services;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// When a build job enters the Failed state, the filter must
/// persist JobIndex.ErrorType = bare exception type name (same convention as heavy
/// tasks), in addition to the pre-existing build-pending cache invalidation.
/// </summary>
[TestFixture]
public class BuildJobFailureFilterTests
{
    private const string JobId = "job-42";
    private const string OrgSlug = "test-org";
    private const string DsSlug = "test-ds";

    /// <summary>JobStorage backed by a scripted connection (GetJobParameter per key).</summary>
    private sealed class TestJobStorage : JobStorage
    {
        private readonly IMonitoringApi _monitor;
        private readonly IStorageConnection _connection;

        public TestJobStorage(IMonitoringApi monitor, IStorageConnection connection)
        {
            _monitor = monitor;
            _connection = connection;
        }

        public override IMonitoringApi GetMonitoringApi() => _monitor;
        public override IStorageConnection GetConnection() => _connection;
    }

    private Mock<ICacheManager> _cacheMock = null!;
    private Mock<IJobIndexWriter> _writerMock = null!;
    private Mock<JobStorageConnection> _connMock = null!;
    private ServiceProvider _provider = null!;
    private JobStorageScope _storageScope = null!;
    private BuildJobFailureFilter _filter = null!;

    [SetUp]
    public void SetUp()
    {
        _cacheMock = new Mock<ICacheManager>();
        _writerMock = new Mock<IJobIndexWriter>();

        var services = new ServiceCollection();
        services.AddSingleton(_cacheMock.Object);
        services.AddSingleton(_writerMock.Object);
        _provider = services.BuildServiceProvider();

        _connMock = new Mock<JobStorageConnection> { CallBase = false };
        _connMock.Setup(c => c.GetJobParameter(JobId, JobParamKeys.OrgSlug)).Returns(OrgSlug);
        _connMock.Setup(c => c.GetJobParameter(JobId, JobParamKeys.DsSlug)).Returns(DsSlug);

        var storage = new TestJobStorage(Mock.Of<IMonitoringApi>(), _connMock.Object);
        _storageScope = new JobStorageScope(storage);

        _filter = new BuildJobFailureFilter(_provider, Mock.Of<ILogger<BuildJobFailureFilter>>());
    }

    [TearDown]
    public void TearDown()
    {
        _storageScope.Dispose();
        _provider.Dispose();
    }

    private ApplyStateContext BuildContext(string methodName, IState newState)
    {
        var method = typeof(HangfireUtils).GetMethod(methodName)!;
        // Hangfire validates args count == method parameter count; the filter only
        // reads Method.Name, so unpopulated (null) arguments of the right count suffice.
        var args = new object?[method.GetParameters().Length];
        var job = new BackgroundJob(JobId, new Job(typeof(HangfireUtils), method, args), DateTime.UtcNow);
        // The public ctor resolves nothing eagerly; connection/transactions are only
        // touched by code paths we do not exercise from the filter.
        return new ApplyStateContext(
            JobStorage.Current, _connMock.Object, Mock.Of<IWriteOnlyTransaction>(), job, newState, "Processing");
    }

    [Test]
    public void OnStateApplied_FailedBuild_WritesErrorTypeNameAndInvalidatesCache()
    {
        var ctx = BuildContext(nameof(HangfireUtils.BuildWrapper),
            new FailedState(new DdbException("gdal cannot open")));

        _filter.OnStateApplied(ctx, Mock.Of<IWriteOnlyTransaction>());

        _writerMock.Verify(w => w.UpdateErrorAsync(JobId, "DdbException", default), Times.Once);
        _cacheMock.Verify(c => c.RemoveByCategoryAsync(
            MagicStrings.BuildPendingTrackerCacheSeed, It.IsAny<string>()), Times.Once);
    }

    [Test]
    public void OnStateApplied_FailedBuildPending_WritesErrorTypeName()
    {
        var ctx = BuildContext(nameof(HangfireUtils.BuildPendingWrapper),
            new FailedState(new InvalidOperationException("boom")));

        _filter.OnStateApplied(ctx, Mock.Of<IWriteOnlyTransaction>());

        _writerMock.Verify(w => w.UpdateErrorAsync(JobId, "InvalidOperationException", default), Times.Once);
    }

    [Test]
    public void OnStateApplied_SucceededState_TouchesNothing()
    {
        var ctx = BuildContext(nameof(HangfireUtils.BuildWrapper), Mock.Of<IState>(s => s.Name == "Succeeded"));

        _filter.OnStateApplied(ctx, Mock.Of<IWriteOnlyTransaction>());

        _writerMock.Verify(w => w.UpdateErrorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _cacheMock.Verify(c => c.RemoveByCategoryAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public void OnStateApplied_NonBuildBuildMethod_TouchesNothing()
    {
        var ctx = BuildContext(nameof(HangfireUtils.CleanupWrapper),
            new FailedState(new Exception("cleanup failed")));

        _filter.OnStateApplied(ctx, Mock.Of<IWriteOnlyTransaction>());

        _writerMock.Verify(w => w.UpdateErrorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _cacheMock.Verify(c => c.RemoveByCategoryAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public void OnStateApplied_MissingOrgDsParams_PreservesEarlyReturn_NoErrorTypeWrite()
    {
        // Models the pre-existing early return: without org/ds slugs neither cache
        // invalidation nor the ErrorType write runs (the writer busts org/ds caches).
        _connMock.Setup(c => c.GetJobParameter(JobId, JobParamKeys.OrgSlug)).Returns((string?)null);

        var ctx = BuildContext(nameof(HangfireUtils.BuildWrapper),
            new FailedState(new DdbException("gdal cannot open")));

        _filter.OnStateApplied(ctx, Mock.Of<IWriteOnlyTransaction>());

        _writerMock.Verify(w => w.UpdateErrorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _cacheMock.Verify(c => c.RemoveByCategoryAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public void OnStateApplied_WriterThrows_DoesNotPropagate()
    {
        _writerMock.Setup(w => w.UpdateErrorAsync(JobId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("db down"));

        var ctx = BuildContext(nameof(HangfireUtils.BuildWrapper),
            new FailedState(new DdbException("gdal cannot open")));

        // State transitions must never break because of the logging path.
        Should.NotThrow(() => _filter.OnStateApplied(ctx, Mock.Of<IWriteOnlyTransaction>()));
    }
}
