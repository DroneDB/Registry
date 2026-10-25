#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Hosting;
using Registry.Web.Test.Support;
using Serilog;
using Shouldly;
using Registry.Test.Common;
using Registry.Web.Data;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// Every host shape builds its container with
/// <see cref="ServiceProviderOptions.ValidateOnBuild"/> and <see cref="ServiceProviderOptions.ValidateScopes"/>,
/// the checks the default host only runs in the Development environment. A service with a missing
/// dependency, or a singleton that captures a scoped service (captive dependency), fails here instead of
/// on the first request or the first job of a deployed instance.
/// <list type="bullet">
/// <item>web host: the real <see cref="Program.CreateHostBuilder"/> + <c>Startup</c> through
/// <see cref="RegistryWebFactory"/>, as a WebServer instance and as a Default instance (web + Hangfire
/// servers);</item>
/// <item>processing node and thumbnail generator: the real builders of <see cref="RegistryHost"/> (the hosts are built by the same code in production and here, no copied registration list).</item>
/// </list>
/// Factory registrations are outside what <c>ValidateOnBuild</c> checks; the <c>*_RejectsCaptiveDependency</c>
/// tests prove the options are applied to each host, so the positive tests cannot pass vacuously. Since
/// The thumbnail generator's handler is constructor-injected, so its container is validated too.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~DiValidationTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class DiValidationTests
{
    private const string CaptiveDependencyMessage = "Cannot consume scoped service";

    private GlobalJobFiltersSnapshot _jobFilters = null!;
    private ILogger _savedLogger = null!;
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _jobFilters = new GlobalJobFiltersSnapshot();
        _savedLogger = Log.Logger;
        _root = Path.Combine(Path.GetTempPath(), "divalidation-di-validation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        // A built host sets the global job filters (RegistryWebFactory restores its own)
        _jobFilters.Restore();

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
    public void ThumbnailGeneratorHost_BuildsWithScopeAndBuildValidation()
    {
        Should.NotThrow(() =>
        {
            using var host = BuildValidatingHost(RegistryHost.CreateThumbnailGeneratorBuilder([]));
        });
    }

    [Test]
    public void ThumbnailGeneratorHost_RejectsCaptiveDependency()
    {
        var ex = Should.Throw<Exception>(() =>
        {
            using var host = BuildValidatingHost(RegistryHost.CreateThumbnailGeneratorBuilder([]), AddCaptiveDependency);
        });
        AllMessages(ex).ShouldContain(CaptiveDependencyMessage);
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
            factory.WithWebHostBuilder(builder => builder.UseDefaultServiceProvider(ValidateAll)));
    }

    /// <summary>Builds (does not start) the processing-node host of <see cref="RegistryHost"/>.</summary>
    private IHost BuildValidatingProcessingNodeHost(Action<IServiceCollection>? configureServices = null) =>
        BuildValidatingHost(RegistryHost.CreateProcessingNodeBuilder([]), configureServices);

    /// <summary>
    /// Builds (does not start) <paramref name="builder"/> with full DI validation, on settings of its own:
    /// the MySQL job storage is created lazily, so its connection string is never opened.
    /// </summary>
    private IHost BuildValidatingHost(IHostBuilder builder, Action<IServiceCollection>? configureServices = null) =>
        builder
            // The test bin folder holds an appsettings.json: keep it out of the host configuration
            .UseContentRoot(_root)
            .UseDefaultServiceProvider(ValidateAll)
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppSettings:HangfireProvider"] = nameof(HangfireProvider.Mysql),
                ["AppSettings:RegistryProvider"] = nameof(DbProvider.Sqlite),
                ["AppSettings:StoragePath"] = Path.Combine(_root, "data"),
                ["AppSettings:DatasetsPath"] = Path.Combine(_root, "datasets"),
                ["AppSettings:TempPath"] = Path.Combine(_root, "temp"),
                ["AppSettings:ThumbnailGeneratorToken"] = "divalidation-thumbnail-token",
                ["ConnectionStrings:RegistryConnection"] = $"Data Source={Path.Combine(_root, "registry.db")}",
                ["ConnectionStrings:HangfireConnection"] = "Server=localhost;Database=hangfire;Uid=unused;Pwd=unused"
            }))
            .ConfigureServices(services => configureServices?.Invoke(services))
            .Build();

    private static void AddCaptiveDependency(IServiceCollection services)
    {
        services.AddScoped<ScopedProbe>();
        services.AddSingleton<CaptiveProbe>();
    }

    private static string AllMessages(Exception ex) =>
        ex is AggregateException aggregate
            ? string.Join(Environment.NewLine, aggregate.Flatten().InnerExceptions.Select(AllMessages))
            : ex.InnerException == null ? ex.Message : ex.Message + Environment.NewLine + AllMessages(ex.InnerException);

    /// <summary>Owns the base factory and the delegated factory carrying the validation options.</summary>
    private sealed class ValidatingWebHost(RegistryWebFactory owner,
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> validating) : IDisposable
    {
        public IServiceProvider Services => validating.Services;

        // Disposing the base factory disposes the factories derived from it
        public void Dispose() => owner.Dispose();
    }

    private sealed class ScopedProbe;

    private sealed class CaptiveProbe(ScopedProbe scoped)
    {
        public ScopedProbe Scoped { get; } = scoped;
    }
}
