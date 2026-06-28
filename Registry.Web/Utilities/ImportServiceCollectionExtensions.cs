#nullable enable
using System;
using Microsoft.Extensions.DependencyInjection;
using Registry.Ports.Import;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Import;

namespace Registry.Web.Utilities;

/// <summary>
/// DI registration for the Import Dataset feature (spec ImportDataset). Registers the import sources,
/// the source factory, the credential protector and the <c>import-dataset</c> heavy tool. Must be
/// called on BOTH the web host and any processing-node host, because the heavy tool runs on the worker.
/// The web-only <c>IImportManager</c> is registered separately in <c>Startup</c>.
/// </summary>
public static class ImportServiceCollectionExtensions
{
    /// <summary>
    /// Registers the import source infrastructure and the import heavy tool.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="appSettings">The application settings.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddImportSources(this IServiceCollection services, AppSettings appSettings)
    {
        var importSettings = appSettings.Import ?? new ImportSettings();

        // Shared Data Protection key ring so the worker can decrypt credentials the web host encrypted.
        services.AddSharedDataProtection(appSettings);

        // HTTP client factory (idempotent if already registered by the host).
        services.AddHttpClient();

        services.AddSingleton(importSettings);
        services.AddSingleton<SsrfGuard>();

        // SSRF-hardened named client used by every import source and the legacy admin import path.
        // Its handler validates the resolved IP at connect time (DNS-rebinding safe), bounds the TCP
        // connect with ConnectTimeoutSeconds, and blocks redirects to private/reserved addresses. The
        // 24h client timeout is a backstop; the per-task transfer timeout is enforced by the import tool.
        services.AddHttpClient(SsrfHttpHandler.HttpClientName, c => c.Timeout = TimeSpan.FromHours(24))
            .ConfigurePrimaryHttpMessageHandler(sp =>
                SsrfHttpHandler.Create(
                    sp.GetRequiredService<SsrfGuard>(),
                    importSettings.MaxRedirects,
                    TimeSpan.FromSeconds(importSettings.ConnectTimeoutSeconds)));
        services.AddSingleton<IImportCredentialProtector, ImportCredentialProtector>();
        services.AddSingleton<IRemoteRegistryClient, RemoteRegistryClient>();

        // One source per type (open-closed: add a new source = one line here).
        services.AddSingleton<IImportSource, RegistryImportSource>();
        services.AddSingleton<IImportSource, ArchiveUrlImportSource>();
        services.AddSingleton<IImportSourceFactory, ImportSourceFactory>();

        // The import heavy tool must run on every host that executes heavy tasks.
        services.AddSingleton<IHeavyTool, ImportDatasetTool>();

        return services;
    }
}
