#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hangfire;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Initialization;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Serilog;
using Shouldly;
using Registry.Test.Common;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-03 (DI validation): every host shape builds its container with
/// <see cref="ServiceProviderOptions.ValidateOnBuild"/> and <see cref="ServiceProviderOptions.ValidateScopes"/>,
/// the checks the default host only runs in the Development environment. A service with a missing
/// dependency, or a singleton that captures a scoped service (captive dependency), fails here instead of
/// on the first request or the first job of a deployed instance.
/// <list type="bullet">
/// <item>web host: the real <see cref="Program.CreateHostBuilder"/> + <c>Startup</c> through
/// <see cref="RegistryWebFactory"/>, as a WebServer instance and as a Default instance (web + Hangfire
/// servers);</item>
/// <item>processing node: the registrations of <c>Program.RunAsProcessingNode</c> (the shared
/// <see cref="ProcessingNodeServiceCollectionExtensions.AddProcessingNodeServices"/> plus the Hangfire
/// provider, the budgeted servers and the jobs initializer) on <see cref="Host.CreateDefaultBuilder()"/>.</item>
/// </list>
/// The thumbnail generator host is not covered: its only application service is a factory registration,
/// which the container cannot validate. Factory registrations in general are outside what
/// <c>ValidateOnBuild</c> checks; the <c>*_RejectsCaptiveDependency</c> tests prove the options are applied
/// to each host, so the positive tests cannot pass vacuously.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq03DiValidationTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class Rq03DiValidationTests
{
    private const string CaptiveDependencyMessage = "Cannot consume scoped service";

    // AddBudgetedHangfireServers hands the processing budget to libddb through the native environment
    // when these are unset: restore them so the hosts leave no process-wide trace.
    private static readonly string[] NativeBudgetVariables =
        ["DDB_BUILD_THREADS", "DDB_COG_CACHE_MB", "DDB_WARP_MEMORY_MB"];

    private HashSet<object> _baselineJobFilters = null!;
    private ILogger _savedLogger = null!;
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _baselineJobFilters = GlobalJobFilters.Filters.Select(f => f.Instance).ToHashSet();
        _savedLogger = Log.Logger;
        _root = Path.Combine(Path.GetTempPath(), "rq03-di-validation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        // AddHangfireProvider adds a global retry filter on every call (RegistryWebFactory restores its own)
        foreach (var filter in GlobalJobFilters.Filters.Select(f => f.Instance)
                     .Where(i => !_baselineJobFilters.Contains(i)).ToList())
            GlobalJobFilters.Filters.Remove(filter);

        Log.Logger = _savedLogger;

        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [TestCase(InstanceType.WebServer)]
    [TestCase(InstanceType.Default)]
    public void WebHost_BuildsWithScopeAndBuildValidation(InstanceType instanceType)
    {
        using var host = CreateValidatingWebHost(instanceType);

        Should.NotThrow(() => _ = host.Services);
    }

    [Test]
    public void WebHost_RejectsCaptiveDependency()
    {
        using var host = CreateValidatingWebHost(InstanceType.WebServer, AddCaptiveDependency);

        var ex = Should.Throw<Exception>(() => _ = host.Services);
        AllMessages(ex).ShouldContain(CaptiveDependencyMessage);
    }

    [Test]
    public void ProcessingNodeHost_BuildsWithScopeAndBuildValidation()
    {
        Should.NotThrow(() =>
        {
            using var host = BuildValidatingProcessingNodeHost();
        });
    }

    [Test]
    public void ProcessingNodeHost_RejectsCaptiveDependency()
    {
        var ex = Should.Throw<Exception>(() =>
        {
            using var host = BuildValidatingProcessingNodeHost(AddCaptiveDependency);
        });
        AllMessages(ex).ShouldContain(CaptiveDependencyMessage);
    }

    private static void ValidateAll(ServiceProviderOptions options)
    {
        options.ValidateOnBuild = true;
        options.ValidateScopes = true;
    }

    /// <summary>The real web host (boots on first access to <c>Services</c>) with full DI validation.</summary>
    private static ValidatingWebHost CreateValidatingWebHost(InstanceType instanceType,
        Action<IServiceCollection>? configureServices = null)
    {
        var factory = new RegistryWebFactory(
            settings: new Dictionary<string, string>
            {
                ["InstanceType"] = instanceType.ToString(),
                // Default instances run real Hangfire servers: keep the per-minute recurring job quiet
                ["AppSettings:RecoverIndexedJobPublicationsCron"] = "disabled"
            },
            configureServices: configureServices);

        return new ValidatingWebHost(factory,
            factory.WithWebHostBuilder(builder => builder.UseDefaultServiceProvider(ValidateAll)),
            new NativeEnvironmentScope(NativeBudgetVariables));
    }

    /// <summary>
    /// Builds (does not start) the processing-node host with the registrations of
    /// <c>Program.RunAsProcessingNode</c>. Hangfire storage is created lazily, so the MySQL connection
    /// string is never opened.
    /// </summary>
    private IHost BuildValidatingProcessingNodeHost(Action<IServiceCollection>? configureServices = null)
    {
        // The budget reaches the environment while the host builds; the host is never started
        using var environment = new NativeEnvironmentScope(NativeBudgetVariables);

        return Host.CreateDefaultBuilder()
            // The test bin folder holds an appsettings.json: keep it out of the host configuration
            .UseContentRoot(_root)
            .UseDefaultServiceProvider(ValidateAll)
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InstanceType"] = nameof(InstanceType.ProcessingNode),
                ["AppSettings:HangfireProvider"] = nameof(HangfireProvider.Mysql),
                ["AppSettings:RegistryProvider"] = nameof(DbProvider.Sqlite),
                ["AppSettings:StoragePath"] = Path.Combine(_root, "data"),
                ["AppSettings:DatasetsPath"] = Path.Combine(_root, "datasets"),
                ["AppSettings:TempPath"] = Path.Combine(_root, "temp"),
                ["ConnectionStrings:RegistryConnection"] = $"Data Source={Path.Combine(_root, "registry.db")}",
                ["ConnectionStrings:HangfireConnection"] = "Server=localhost;Database=hangfire;Uid=unused;Pwd=unused"
            }))
            .UseSerilog(Program.ConfigureSerilog)
            .ConfigureServices((context, services) =>
            {
                var configuration = context.Configuration;
                var appSettings = configuration.GetSection("AppSettings").Get<AppSettings>()!;
                var budget = ProcessingBudget.Derive(HostFacts.Current(), appSettings.ProcessingBudget,
                    appSettings.WorkerThreads);

                // Same calls, same order as Program.RunAsProcessingNode
                services.AddProcessingNodeServices(configuration, appSettings);
                services.AddHangfireProvider(appSettings, configuration);
                services.AddBudgetedHangfireServers(budget);
                services.AddHostedService<HangfireJobsHostedService>();

                configureServices?.Invoke(services);
            })
            .Build();
    }

    private static void AddCaptiveDependency(IServiceCollection services)
    {
        services.AddScoped<ScopedProbe>();
        services.AddSingleton<CaptiveProbe>();
    }

    private static string AllMessages(Exception ex) =>
        ex is AggregateException aggregate
            ? string.Join(Environment.NewLine, aggregate.Flatten().InnerExceptions.Select(AllMessages))
            : ex.InnerException == null ? ex.Message : ex.Message + Environment.NewLine + AllMessages(ex.InnerException);

    /// <summary>
    /// Owns the base factory, the delegated factory carrying the validation options and the native
    /// environment the Default instance writes its processing budget to while it builds.
    /// </summary>
    private sealed class ValidatingWebHost(RegistryWebFactory owner,
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> validating,
        NativeEnvironmentScope environment) : IDisposable
    {
        public IServiceProvider Services => validating.Services;

        public void Dispose()
        {
            // Disposing the base factory disposes the factories derived from it
            owner.Dispose();
            environment.Dispose();
        }
    }

    private sealed class ScopedProbe;

    private sealed class CaptiveProbe(ScopedProbe scoped)
    {
        public ScopedProbe Scoped { get; } = scoped;
    }
}
