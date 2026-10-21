#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Web.Services.Adapters;
using Registry.Web.Utilities;
using Serilog;
using Serilog.Events;

namespace Registry.Web.Hosting;

/// <summary>
/// Builds the host of every instance type (RQ-07): one builder with the common parts (configuration of
/// the instance type, Serilog, the libddb runtime first, the options, the startup task runner), then the
/// modules of the role.
/// </summary>
public static class RegistryHost
{
    /// <summary>The logger category of the start-up messages of <c>Program</c> and <c>Startup</c>.</summary>
    public const string StartupLogCategory = "Registry.Startup";

    /// <summary>
    /// The builder every host starts from: <paramref name="role"/> in the configuration, Serilog, and the
    /// hosted services every role has, in this order: the libddb runtime (the first hosted service: it
    /// starts first and stops last, XI-07; the native log sink, XI-08) and the startup task runner. The
    /// role's modules add the options (<see cref="RegistryOptions.AddRegistryOptions"/>).
    /// </summary>
    /// <param name="role">The instance type; null keeps the one of the configuration (default: <see cref="InstanceType.Default"/>).</param>
    /// <param name="args">Host arguments (e.g. <c>--urls</c>).</param>
    public static IHostBuilder CreateBuilder(InstanceType? role, string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((_, config) =>
            {
                // Layered over the configuration sources; a test host layers its own settings on top
                if (role is { } instanceType)
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { Program.InstanceTypeKey, instanceType.ToString() }
                    });
            })
            .UseSerilog(ConfigureSerilog)
            .ConfigureServices((context, services) =>
            {
                services.AddDdbRuntime<NativeDdbRuntime>();
                services.AddStartupTaskRunner();
            });

    /// <summary>The web server (and the all-in-one instance): <see cref="Startup"/> composes the web modules.</summary>
    public static IHostBuilder CreateWebBuilder(InstanceType? role, string[] args) =>
        CreateBuilder(role, args).ConfigureWebHostDefaults(web => web.UseStartup<Startup>());

    /// <summary>The processing node: the shared job services, the job storage and the budgeted Hangfire servers; no web server.</summary>
    public static IHostBuilder CreateProcessingNodeBuilder(string[] args) =>
        CreateBuilder(InstanceType.ProcessingNode, args)
            .ConfigureServices((context, services) =>
            {
                services.AddProcessingNodeServices(context.Configuration);
                services.AddRegistryJobs(context.Configuration);
                services.AddJobServers();
            });

    /// <summary>
    /// The thumbnail generator: <c>POST /generate-thumbnail</c> (shared token, readable roots: the datasets
    /// and temp folders) and the liveness probe.
    /// </summary>
    public static IHostBuilder CreateThumbnailGeneratorBuilder(string[] args) =>
        CreateBuilder(InstanceType.ThumbnailGenerator, args)
            .ConfigureServices((context, services) =>
            {
                services.AddRegistryOptions(context.Configuration, InstanceType.ThumbnailGenerator);
                services.AddSingleton<IDdbWrapper, NativeDdbWrapper>();
                services.AddSingleton<ThumbnailRequestHandler>();
            })
            .ConfigureWebHostDefaults(web => web.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => MapThumbnailGeneratorEndpoints(endpoints,
                    app.ApplicationServices.GetRequiredService<ThumbnailRequestHandler>()));
            }));

    /// <summary>The builder of <paramref name="role"/>.</summary>
    public static IHostBuilder CreateBuilderFor(InstanceType role, string[] args) => role switch
    {
        InstanceType.Default or InstanceType.WebServer => CreateWebBuilder(role, args),
        InstanceType.ProcessingNode => CreateProcessingNodeBuilder(args),
        InstanceType.ThumbnailGenerator => CreateThumbnailGeneratorBuilder(args),
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Invalid instance type")
    };

    /// <summary>
    /// The thumbnail generator host routes:
    /// <list type="bullet">
    /// <item><c>POST /generate-thumbnail</c> (form: path, size; header: the shared token); the response is the
    /// binary thumbnail from <c>DDBWrapper.GenerateThumbnail</c>;</item>
    /// <item><c>GET /health/live</c>, the anonymous liveness probe used by the container HEALTHCHECK.</item>
    /// </list>
    /// </summary>
    internal static void MapThumbnailGeneratorEndpoints(IEndpointRouteBuilder endpoints,
        ThumbnailRequestHandler handler)
    {
        endpoints.MapPost(ThumbnailRequestHandler.Route, handler.HandleAsync);
        endpoints.MapLiveness();
    }

    /// <summary>
    /// The one Serilog setup of every host (web server, processing node, thumbnail generator): sinks and
    /// levels come from the <c>Serilog</c> configuration section, so all instances log the same way. The
    /// file sink path gets the instance type (<see cref="InstanceLogPaths"/>): containers sharing the
    /// storage folder never write the same file. The start-up messages
    /// (<see cref="StartupLogCategory"/>) are always written, whatever the configured level.
    /// </summary>
    internal static void ConfigureSerilog(HostBuilderContext context, IServiceProvider services,
        LoggerConfiguration configuration) =>
        configuration
            .ReadFrom.Configuration(InstanceLogPaths.Apply(context.Configuration))
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .MinimumLevel.Override(StartupLogCategory, LogEventLevel.Information);

    /// <summary>
    /// The logger used before a host exists (the command line, the storage folder setup, a host that
    /// fails to build): the console, Information and above, errors on stderr. A host's
    /// <c>UseSerilog</c> replaces it with the configured logger.
    /// </summary>
    public static void UseBootstrapLogger() => Log.Logger = CommandLineLogging().CreateBootstrapLogger();

    /// <summary>
    /// The logger of a maintenance command's result (<c>--check</c>, <c>--reset-admin-password</c>): the
    /// console like the bootstrap logger, kept when the command builds a host (whose <c>UseSerilog</c>
    /// replaces the static logger with the configured one).
    /// </summary>
    public static Serilog.Core.Logger CreateCommandLineLogger() => CommandLineLogging()
        .Enrich.WithProperty(Serilog.Core.Constants.SourceContextPropertyName, StartupLogCategory)
        .CreateLogger();

    private static LoggerConfiguration CommandLineLogging() =>
        new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                standardErrorFromLevel: LogEventLevel.Error);

    /// <summary>The start-up logger of code that runs outside a container (<c>Program</c>).</summary>
    public static ILogger StartupLog => Log.ForContext(Serilog.Core.Constants.SourceContextPropertyName, StartupLogCategory);
}
