#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Registry.Adapters.Testing;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-08 (XI-01, XI-02, XI-06, XI-07, XI-08), the host side of libddb ABI 1.1: the process lifecycle
/// runs in the order libddb requires (log sink and configuration before DDBRegisterProcess, checked;
/// DDBShutdown after every hosted service stopped), its timeouts nest inside the host's and Docker's,
/// and the typed libddb errors reach clients with their HTTP status.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq08DdbRuntimeTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class Rq08DdbRuntimeTests
{
    private static DdbRuntimeService Service(FakeDdbRuntime runtime, DdbRuntimeOptions? options = null,
        ILoggerFactory? loggerFactory = null) =>
        new(runtime, Microsoft.Extensions.Options.Options.Create(options ?? new DdbRuntimeOptions()),
            new DdbLogSink(loggerFactory ?? NullLoggerFactory.Instance), NullLogger<DdbRuntimeService>.Instance);

    [Test]
    public async Task Starting_SetsTheLogSinkAndTheConfiguration_BeforeRegisterProcess()
    {
        var runtime = new FakeDdbRuntime();
        var options = new DdbRuntimeOptions { CpuPoolSize = 6, IoThreads = 2, MinFreeDiskMb = 1024, CacheDir = "/data/temp/ddb-cache" };

        await Service(runtime, options).StartingAsync(CancellationToken.None);

        runtime.Calls.ShouldBe(["SetLogSink", "Configure", "RegisterProcess"]);
        runtime.Settings.ShouldBe(new DdbProcessSettings(6, 2, 1024, "/data/temp/ddb-cache"));
        runtime.LogSink.ShouldBeOfType<DdbLogSink>();
    }

    [Test]
    public async Task Stopped_ShutsLibddbDown_WithinItsBudget()
    {
        var runtime = new FakeDdbRuntime();
        var service = Service(runtime);
        await service.StartingAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);
        runtime.Calls.ShouldNotContain("Shutdown", "StopAsync runs while other services still stop");

        await service.StoppedAsync(CancellationToken.None);
        runtime.Calls[^1].ShouldBe("Shutdown");
        runtime.ShutdownTimeout.ShouldBe(ShutdownTimeouts.Ddb);
    }

    /// <summary>The web host: the runtime service is the first hosted service, so it starts first and stops last.</summary>
    [Test]
    public async Task WebHost_RunsTheLifecycle_AndRegistersTheRuntimeFirst()
    {
        var factory = new RegistryWebFactory();
        var runtime = factory.DdbRuntime;
        try
        {
            using var client = factory.CreateClient();

            factory.Services.GetServices<IHostedService>().First().ShouldBeOfType<DdbRuntimeService>();
            runtime.Calls.ShouldBe(["SetLogSink", "Configure", "RegisterProcess"]);
            runtime.Settings!.CacheDir.ShouldBe(Path.GetFullPath(
                Path.Combine(factory.Services.GetRequiredService<IOptions<AppSettings>>().Value.TempPath,
                    DdbRuntimeOptions.DefaultCacheFolderName)));
            factory.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout
                .ShouldBe(ShutdownTimeouts.Host);
        }
        finally
        {
            await factory.DisposeAsync();
        }

        runtime.Calls[^1].ShouldBe("Shutdown");
    }

    [Test]
    public void InvalidOptions_FailTheStart()
    {
        using var factory = new RegistryWebFactory(new System.Collections.Generic.Dictionary<string, string>
        {
            ["Ddb:IoThreads"] = "0"
        });

        Should.Throw<OptionsValidationException>(() => factory.CreateClient()).Message.ShouldContain("IoThreads");
    }

    [TestCase(null, "/data/temp", "/data/temp/ddb-cache")]
    [TestCase("/var/cache/ddb", "/data/temp", "/var/cache/ddb")]
    [TestCase(null, null, null)]
    public void CacheDir_DefaultsUnderTheTempPath(string? configured, string? tempPath, string? expected)
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("POSIX paths");

        DdbRuntimeServiceCollectionExtensions.ResolveCacheDir(configured, tempPath).ShouldBe(expected);
    }

    [Test]
    public void ShutdownStages_Nest()
    {
        (ShutdownTimeouts.HangfireServer + ShutdownTimeouts.Ddb).ShouldBeLessThan(ShutdownTimeouts.Host);
        ShutdownTimeouts.Host.ShouldBeLessThan(ShutdownTimeouts.ComposeStopGracePeriod);
    }

    /// <summary>Docker kills a container after its stop_grace_period: every Registry service waits longer than the host.</summary>
    [TestCase("docker/production/docker-compose.yml", 3)]
    [TestCase("docker/testing/docker-compose.yml", 1)]
    public void ComposeFiles_GiveTheRegistryServicesTheGracePeriod(string relativePath, int services)
    {
        Assume.That(RepositoryFiles.Root, Is.Not.Null, "repository sources not available next to the test output");
        var compose = File.ReadAllText(RepositoryFiles.PathOf(relativePath));

        Regex.Count(compose, @"image: dronedb/registry:").ShouldBe(services);
        var periods = Regex.Matches(compose, @"stop_grace_period: (\d+)s")
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        periods.Length.ShouldBe(services);
        periods.ShouldAllBe(p => TimeSpan.FromSeconds(p) >= ShutdownTimeouts.ComposeStopGracePeriod);
    }

    /// <summary>XI-02: the libddb ABI 1.1 error codes reach the client with a status that says whether to retry.</summary>
    [Test]
    public void TypedLibddbErrors_HaveTheirHttpStatus()
    {
        var invalid = ApiExceptionClassifier.Classify(new DdbInvalidArgumentsException("bad hash"), true);
        (invalid.StatusCode, invalid.NoRetry, invalid.Message).ShouldBe((StatusCodes.Status400BadRequest, true, "bad hash"));

        var notFound = ApiExceptionClassifier.Classify(new DdbNotFoundException("no build"), true);
        (notFound.StatusCode, notFound.NoRetry).ShouldBe((StatusCodes.Status404NotFound, true));

        var retryable = ApiExceptionClassifier.Classify(new DdbRetryableException("disk full", "noSpaceOnDevice"), true);
        (retryable.StatusCode, retryable.NoRetry).ShouldBe((StatusCodes.Status503ServiceUnavailable, false));
        retryable.RetryAfterSeconds.ShouldNotBeNull();

        var shutdown = ApiExceptionClassifier.Classify(new DdbShutdownException("shutting down"), true);
        (shutdown.StatusCode, shutdown.NoRetry).ShouldBe((StatusCodes.Status503ServiceUnavailable, false));

        // A cancellation is not a server error
        ApiExceptionClassifier.Classify(new DdbCanceledException("canceled"), true).StatusCode
            .ShouldBe(StatusCodes.Status408RequestTimeout);
        // The generic native failure stays a 500 with the generic message
        ApiExceptionClassifier.Classify(new DdbException("engine detail"), true).Message
            .ShouldBe(ApiExceptionClassifier.InternalServerErrorMessage);
    }

    [Test]
    public void LogSink_MapsTheLevels_AndAsksLibddbForTheLoggersMinimum()
    {
        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(new ThrowingProvider()));
        var sink = new DdbLogSink(loggerFactory);

        sink.MinimumLevel.ShouldBe(DdbLogLevel.Info);
        DdbLogSink.ToLogLevel(DdbLogLevel.Warning).ShouldBe(LogLevel.Warning);
        DdbLogSink.ToLogLevel(DdbLogLevel.Fatal).ShouldBe(LogLevel.Critical);

        // Called on native threads: a logger that throws never reaches libddb
        Should.NotThrow(() => sink.Write(DdbLogLevel.Error, "boom", "gdal", 42));
    }

    private sealed class ThrowingProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ThrowingLogger();

        public void Dispose()
        {
        }

        private sealed class ThrowingLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => throw new InvalidOperationException("sink failure");
        }
    }
}
