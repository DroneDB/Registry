#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Registry.Adapters.Testing;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Hosting;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Initialization;
using Registry.Web.Test.Support;
using Serilog;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-07 (composition root and startup):
/// <list type="bullet">
/// <item>one global retry policy: building a host replaces it instead of adding one more (red before
/// RQ-07: every host added an <see cref="AutomaticRetryAttribute"/>);</item>
/// <item>the startup tasks run in their order, before any hosted service starts, and a failure stops
/// the start; a background startup task runs once the host has started (no delay) and stops with it;</item>
/// <item>every host is built by <see cref="RegistryHost"/>: the libddb runtime first, the startup task
/// runner second (so the node's caches and recurring jobs are set up before its Hangfire servers start),
/// the role's tasks only (the nodes never migrate the databases);</item>
/// <item>the thumbnail generator refuses to start without its token (an options error at start).</item>
/// </list>
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq07HostTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public class Rq07HostTests
{
    private GlobalJobFiltersSnapshot _jobFilters = null!;
    private ILogger _savedLogger = null!;
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _jobFilters = new GlobalJobFiltersSnapshot();
        _savedLogger = Log.Logger;
        _root = Path.Combine(Path.GetTempPath(), "rq07-hosts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        _jobFilters.Restore();
        Log.Logger = _savedLogger;
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Test]
    public void GlobalRetryPolicy_IsOne_WhateverTheNumberOfHosts()
    {
        using var first = new RegistryWebFactory();
        using var second = new RegistryWebFactory();
        _ = first.Services;
        _ = second.Services;

        var retries = GlobalJobFilters.Filters.Select(f => f.Instance).OfType<AutomaticRetryAttribute>().ToList();
        retries.ShouldHaveSingleItem().Attempts.ShouldBe(1);
    }

    [Test]
    public async Task StartupTasks_RunInOrder_BeforeAnyHostedServiceStarts()
    {
        var events = new List<string>();
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(events);
                // First registered hosted service: its StartAsync is the first one
                services.AddHostedService<RecordingHostedService>();
                services.AddStartupTaskRunner();
                services.AddStartupTask<LateTask>();
                services.AddStartupTask<EarlyTask>();
            })
            .Build();

        await host.StartAsync();
        await host.StopAsync();

        events.ShouldBe(["early", "late", "hosted service started"]);
    }

    [Test]
    public async Task FailingStartupTask_StopsTheStart()
    {
        var events = new List<string>();
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(events);
                services.AddHostedService<RecordingHostedService>();
                services.AddStartupTaskRunner();
                services.AddStartupTask<FailingTask>();
            })
            .Build();

        (await Should.ThrowAsync<InvalidOperationException>(() => host.StartAsync())).Message.ShouldBe("rq07 task failed");
        events.ShouldBeEmpty("no hosted service starts after a failed startup task");
    }

    /// <summary>The visibility preload waited 5 s "to give the host a moment": now it starts when the host has started.</summary>
    [Test]
    public async Task BackgroundStartupTask_RunsAfterTheHostStarted_AndStopsWithIt()
    {
        var probe = new BackgroundProbe();
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(probe);
                services.AddStartupTaskRunner();
                services.AddBackgroundStartupTask<BlockingBackgroundTask>();
            })
            .Build();

        await host.StartAsync();
        (await probe.Running.Task.WaitAsync(TimeSpan.FromSeconds(30))).ShouldBeTrue(
            "the background task runs once the host has started");

        await host.StopAsync();
        probe.Canceled.Task.IsCompletedSuccessfully.ShouldBeTrue("stopping the host cancels the background task");
    }

    [Test]
    public void ProcessingNode_RuntimeAndRunnerFirst_CachesAndRecurringJobs_NoMigration()
    {
        var services = Capture(RegistryHost.CreateProcessingNodeBuilder([]));

        HostedServices(services).Take(2).ShouldBe([typeof(DdbRuntimeService), typeof(StartupTaskRunner)]);
        StartupTasks(services).ShouldBe([typeof(CacheInitializer), typeof(HangfireJobsInitializer)], ignoreOrder: true);
        services.Count(d => d.ServiceType == typeof(IHostedService)).ShouldBeGreaterThanOrEqualTo(4,
            "the two Hangfire servers come after the runtime and the runner");
    }

    [Test]
    public void WebServer_RuntimeAndRunnerFirst_ItsTasks()
    {
        var services = Capture(RegistryHost.CreateWebBuilder(InstanceType.WebServer, []));

        HostedServices(services).Take(2).ShouldBe([typeof(DdbRuntimeService), typeof(StartupTaskRunner)]);
        StartupTasks(services).ShouldBe(
            [typeof(ThreadPoolInitializer), typeof(DatabaseInitializer), typeof(CacheInitializer), typeof(HangfireJobsInitializer)],
            ignoreOrder: true);
        services.Where(d => d.ServiceType == typeof(IBackgroundStartupTask)).Select(d => d.ImplementationType)
            .ShouldBe([typeof(DatasetVisibilityCacheInitializer)]);
    }

    [Test]
    public async Task ThumbnailGenerator_WithoutToken_DoesNotStart()
    {
        using var host = ThumbnailHost(token: "");

        var error = await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync());
        error.Message.ShouldContain("ThumbnailGeneratorToken");
    }

    [Test]
    public async Task ThumbnailGenerator_StartsAndStops_WithItsLibddbLifecycle()
    {
        var runtime = new FakeDdbRuntime();
        using var host = ThumbnailHost(token: "rq07-thumbnail-token", runtime);

        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using (var client = new HttpClient())
            (await client.GetAsync(address + "/health/live")).IsSuccessStatusCode.ShouldBeTrue();
        await host.StopAsync();

        runtime.Calls.ShouldBe(["SetLogSink", "Configure", "RegisterProcess", "Shutdown"]);
    }

    private IHost ThumbnailHost(string token, FakeDdbRuntime? runtime = null) =>
        RegistryHost.CreateThumbnailGeneratorBuilder(["--urls", "http://127.0.0.1:0"])
            .UseContentRoot(_root)
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Settings(token)))
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDdbRuntime>();
                services.AddSingleton<IDdbRuntime>(runtime ?? new FakeDdbRuntime());
                services.RemoveAll<IDdbWrapper>();
                services.AddSingleton<IDdbWrapper>(new FakeDdbWrapper());
            })
            .Build();

    /// <summary>The services of <paramref name="builder"/> as registered (the host is built, not started).</summary>
    private List<ServiceDescriptor> Capture(IHostBuilder builder)
    {
        List<ServiceDescriptor> captured = [];
        using var host = builder
            .UseContentRoot(_root)
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Settings("rq07-token")))
            .ConfigureServices(services => captured = services.ToList())
            .Build();
        return captured;
    }

    private Dictionary<string, string?> Settings(string token) => new()
    {
        ["AppSettings:Secret"] = "rq07-secret",
        ["AppSettings:TokenExpirationInDays"] = "7",
        ["AppSettings:AuthCookieName"] = "jwtToken",
        ["AppSettings:BatchTokenLength"] = "32",
        ["AppSettings:UploadBatchTimeout"] = "01:00:00",
        ["AppSettings:RandomDatasetNameLength"] = "16",
        ["AppSettings:DefaultAdmin:Email"] = "admin@example.com",
        ["AppSettings:DefaultAdmin:UserName"] = "admin",
        ["AppSettings:DefaultAdmin:Password"] = "password123",
        ["AppSettings:HangfireProvider"] = "Mysql",
        ["AppSettings:RegistryProvider"] = "Sqlite",
        ["AppSettings:AuthProvider"] = "Sqlite",
        ["AppSettings:StoragePath"] = Path.Combine(_root, "data"),
        ["AppSettings:DatasetsPath"] = Path.Combine(_root, "datasets"),
        ["AppSettings:CachePath"] = Path.Combine(_root, "cache"),
        ["AppSettings:TempPath"] = Path.Combine(_root, "temp"),
        ["AppSettings:ThumbnailGeneratorToken"] = token,
        ["ConnectionStrings:RegistryConnection"] = $"Data Source={Path.Combine(_root, "registry.db")}",
        ["ConnectionStrings:IdentityConnection"] = $"Data Source={Path.Combine(_root, "identity.db")}",
        ["ConnectionStrings:HangfireConnection"] = "Server=localhost;Database=hangfire;Uid=unused;Pwd=unused"
    };

    private static IEnumerable<Type?> HostedServices(IEnumerable<ServiceDescriptor> services) =>
        services.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => d.ImplementationType);

    private static IEnumerable<Type?> StartupTasks(IEnumerable<ServiceDescriptor> services) =>
        services.Where(d => d.ServiceType == typeof(IStartupTask)).Select(d => d.ImplementationType);

    private sealed class RecordingHostedService(List<string> events) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            events.Add("hosted service started");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EarlyTask(List<string> events) : IStartupTask
    {
        public int Order => 10;

        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            events.Add("early");
            return Task.CompletedTask;
        }
    }

    private sealed class LateTask(List<string> events) : IStartupTask
    {
        public int Order => 20;

        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            events.Add("late");
            return Task.CompletedTask;
        }
    }

    private sealed class FailingTask : IStartupTask
    {
        public int Order => 0;

        public Task ExecuteAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("rq07 task failed");
    }

    private sealed class BackgroundProbe
    {
        public TaskCompletionSource<bool> Running { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BlockingBackgroundTask(BackgroundProbe probe, IHostApplicationLifetime lifetime)
        : IBackgroundStartupTask
    {
        public async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            probe.Running.SetResult(lifetime.ApplicationStarted.IsCancellationRequested);

            // Waits for the host to stop
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (stoppingToken.Register(() => stopped.SetResult()))
                await stopped.Task;

            probe.Canceled.SetResult();
            stoppingToken.ThrowIfCancellationRequested();
        }
    }
}
