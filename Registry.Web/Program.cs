using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using Hangfire;
using Microsoft.AspNetCore.Builder;
using Registry.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeMapping;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Adapters;
using Registry.Adapters.DroneDB;
using Registry.Adapters.Thumbnail;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Initialization;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Serilog;

namespace Registry.Web;

public class Program
{
    private const int DefaultPort = 5000;
    private const string DefaultHost = "localhost";

    /// <summary>Configuration key holding the <see cref="InstanceType"/> of the running host.</summary>
    internal const string InstanceTypeKey = "InstanceType";

    /// <summary>Environment variable <c>--reset-admin-password</c> reads the new password from (else stdin).</summary>
    public const string AdminPasswordEnvironmentVariable = "REGISTRY_ADMIN_PASSWORD";

    /// <summary>Entry point; the process exit code is one of <see cref="ExitCodes"/>.</summary>
    public static int Main(string[] args)
    {
#if DEBUG_EF
            // EF core tools compatibility
            if (IsEfTool(args))
            {
                RunEfToolsHost(args);
                return ExitCodes.Success;
            }
#endif

        return Run(args, StartInstance, ReadAdminPasswordFromConsole);
    }

    /// <summary>
    /// Parses the command line and runs it: <see cref="ExitCodes.InvalidArguments"/> when it cannot be
    /// parsed (help and version requests succeed), otherwise the code of <see cref="RunOptions"/>.
    /// </summary>
    /// <param name="args">The command line.</param>
    /// <param name="startInstance">Runs the host of the instance type until shutdown (injectable in tests).</param>
    /// <param name="readAdminPassword">Reads the new password for <c>--reset-admin-password</c>; <c>null</c> = none.</param>
    internal static int Run(string[] args, Func<Options, int> startInstance, Func<string> readAdminPassword)
    {
        // Parser.Default's settings, with the help writer bound to the current stderr (the static
        // default keeps the first one it saw)
        using var parser = new Parser(settings => settings.HelpWriter = Console.Error);

        return parser.ParseArguments<Options>(args)
            .MapResult(opts => RunOptions(opts, startInstance, readAdminPassword),
                errors => errors.IsHelp() || errors.IsVersion() ? ExitCodes.Success : ExitCodes.InvalidArguments);
    }

    private static bool FixPath()
    {
        var ddbFolder = CommonUtils.FindDdbFolder();

        if (ddbFolder == null)
        {
            Console.WriteLine(" !> ddb library not found in PATH");
            return false;
        }

        Console.WriteLine(" ?> Found ddl library in {0}", ddbFolder);

        Environment.SetEnvironmentVariable("PATH",
            $"{ddbFolder}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");

        return true;
    }

    private static int RunOptions(Options opts, Func<Options, int> startInstance, Func<string> readAdminPassword)
    {
        var appVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        var appName = AppDomain.CurrentDomain.FriendlyName;

        CommonUtils.WriteLineColor($"\n\t\t*** {appName} - v{appVersion} ***\n", ConsoleColor.Green);

#if DEBUG
        Console.WriteLine(" ?> Running in DEBUG");
#else
            Console.WriteLine(" ?> Running in RELEASE");
#endif

        if (!VerifyOptions(opts)) return ExitCodes.InvalidArguments;

        // Maintenance commands: no host, no storage folder setup
        if (opts.CheckConfig)
            return CheckConfiguration(opts.StorageFolder);

        if (opts.ResetAdminPassword)
            return ResetAdminPassword(opts.StorageFolder, readAdminPassword);

        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            CommonUtils.WriteLineColor("\n -> Exiting...\n", ConsoleColor.Yellow);
        };

        // If we need to initialize ddb from the storage folder
        var lateDdbInit = false;

        if (opts.ResetDdb)
            lateDdbInit = true;
        else
        {
            // Ensure the ddb.dll is in the PATH and fix it if needed (only on windows)
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                lateDdbInit = !FixPath();
        }

        opts.StorageFolder = Path.GetFullPath(opts.StorageFolder);
        Directory.CreateDirectory(opts.StorageFolder);
        Environment.CurrentDirectory = opts.StorageFolder;

        Console.WriteLine(" ?> Using storage folder '{0}'", opts.StorageFolder);

        // Only the webserver owns the Hub (ClientApp) folder. Processing nodes and
        // thumbnail generators share the same StorageFolder in production deployments,
        // so letting them extract/upgrade ClientApp.zip would race against the webserver
        // (Directory.Delete + ZipArchive.ExtractToDirectory concurrently on the same path).
        var deployHub = opts.InstanceType is InstanceType.Default or InstanceType.WebServer;

        if (lateDdbInit)
        {
            if (!SetupStorageFolder(opts.StorageFolder, opts.ResetHub, opts.ResetDdb, true, deployHub))
            {
                CommonUtils.WriteLineColor(" !> Failed to setup storage folder", ConsoleColor.Red);
                return ExitCodes.Failure;
            }

            if (!InitializeDdb())
            {
                CommonUtils.WriteLineColor(" !> Failed to initialize DroneDB", ConsoleColor.Red);
                return ExitCodes.Failure;
            }
        }
        else
        {
            if (!InitializeDdb())
            {
                CommonUtils.WriteLineColor(" !> Failed to initialize DroneDB", ConsoleColor.Red);
                return ExitCodes.Failure;
            }

            if (!SetupStorageFolder(opts.StorageFolder, opts.ResetHub, deployHub: deployHub))
            {
                CommonUtils.WriteLineColor(" !> Failed to setup storage folder", ConsoleColor.Red);
                return ExitCodes.Failure;
            }
        }

        return RunInstance(opts, startInstance);
    }

    /// <summary>
    /// Runs the instance host until it shuts down: its exit code, or <see cref="ExitCodes.Failure"/>
    /// when it throws (logged as fatal). The static logger is flushed either way.
    /// </summary>
    internal static int RunInstance(Options opts, Func<Options, int> startInstance)
    {
        try
        {
            return startInstance(opts);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(" !> Error while running application");
            Console.Error.WriteLine(ex.Message);
            Log.Fatal(ex, "Host terminated unexpectedly");
            return ExitCodes.Failure;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>Starts the host of <see cref="Options.InstanceType"/> and blocks until it shuts down.</summary>
    private static int StartInstance(Options opts)
    {
        switch (opts.InstanceType)
        {
            case InstanceType.Default:
            case InstanceType.WebServer:
                return RunAsWebApplication(opts);

            case InstanceType.ProcessingNode:
                return RunAsProcessingNode(opts);

            case InstanceType.ThumbnailGenerator:
                return RunAsThumbnailGenerator(opts);

            default:
                CommonUtils.WriteLineColor(" !> Invalid instance type", ConsoleColor.Red);
                return ExitCodes.InvalidArguments;
        }
    }

    /// <summary>Layers the instance type over the host configuration (read by Startup and the log setup).</summary>
    private static IHostBuilder WithInstanceType(IHostBuilder builder, InstanceType instanceType) =>
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string>
            {
                { InstanceTypeKey, instanceType.ToString() }
            }));

    private static int RunAsThumbnailGenerator(Options opts)
    {
        Console.WriteLine(" ?> Using address '{0}'", opts.Address);
        Console.WriteLine(" -> Starting thumbnail generator");

        var args = new[] { "--urls", $"http://{opts.Address}" };

        using var host = WithInstanceType(Host.CreateDefaultBuilder(args), opts.InstanceType)
            .UseSerilog(ConfigureSerilog)
            .ConfigureServices((context, services) =>
            {
                services.AddDdbRuntime<NativeDdbRuntime>(context.Configuration);
                services.AddSingleton(sp => CreateThumbnailRequestHandler(context.Configuration,
                    sp.GetRequiredService<ILogger<ThumbnailRequestHandler>>()));
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.Configure(app =>
                {
                    var handler = app.ApplicationServices.GetRequiredService<ThumbnailRequestHandler>();

                    app.UseRouting();
                    app.UseEndpoints(endpoints => MapThumbnailGeneratorEndpoints(endpoints, handler));
                });
            })
            .Build();

        // Fail closed: without the shared token the endpoint would read files for anyone
        try
        {
            host.Services.GetRequiredService<ThumbnailRequestHandler>();
        }
        catch (ArgumentException ex)
        {
            CommonUtils.WriteLineColor(
                $" !> Cannot start the thumbnail generator: {ex.Message} Set AppSettings:ThumbnailGeneratorToken " +
                "to the value the web server uses (both read the same appsettings.json).", ConsoleColor.Red);
            host.Services.GetRequiredService<ILogger<Program>>()
                .LogCritical(ex, "Thumbnail generator not started: {Message}", ex.Message);
            return ExitCodes.Failure;
        }

        host.Run();
        return ExitCodes.Success;
    }

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
    /// The thumbnail endpoint handler: the shared token and the readable roots (datasets and temp
    /// folders) come from the <c>AppSettings</c> section.
    /// </summary>
    private static ThumbnailRequestHandler CreateThumbnailRequestHandler(IConfiguration configuration,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        var settings = configuration.GetSection("AppSettings").Get<AppSettings>() ?? new AppSettings();

        return new ThumbnailRequestHandler(settings.ThumbnailGeneratorToken,
            [settings.DatasetsPath, settings.TempPath], new NativeDdbWrapper(), logger);
    }

    /// <summary>
    /// Runs the application as a processing node (Hangfire worker only).
    /// This mode executes background jobs but does not host a web server.
    ///
    /// Recurring jobs executed:
    /// - CleanupExpiredJobs: Daily cleanup of expired Hangfire jobs
    /// - JobIndexSyncService.SyncJobIndexStates: Sync job index every 5 minutes
    /// - BuildPendingService.ProcessPendingBuilds: Process pending DDB builds every minute
    /// - OrphanedDatasetCleanupService.CleanupOrphanedFoldersAsync: Daily cleanup of orphaned dataset folders
    ///
    /// On-demand jobs:
    /// - DatasetCleanupService.CleanupDeletedDatasetAsync: Cleanup after dataset deletion
    ///
    /// NOTE: When adding new recurring jobs or services, ensure all dependencies are registered here.
    /// Only minimal services required for job execution are registered to reduce memory footprint.
    /// </summary>
    private static int RunAsProcessingNode(Options opts)
    {
        Console.WriteLine(" -> Starting processing node");

        var host = WithInstanceType(Host.CreateDefaultBuilder(), opts.InstanceType)
            .UseSerilog(ConfigureSerilog)
            .ConfigureServices((hostContext, services) =>
            {
                var configuration = hostContext.Configuration;

                // libddb's lifecycle first: it starts before and stops after every other hosted service
                services.AddDdbRuntime<NativeDdbRuntime>(configuration);

                var appSettingsSection = configuration.GetSection("AppSettings");

                var appSettings = appSettingsSection.Get<AppSettings>();

                if (appSettings.HangfireProvider == HangfireProvider.InMemory)
                    throw new NotSupportedException("Hangfire in-memory provider is not supported for processing node");

                var budget = ProcessingBudget.Derive(HostFacts.Current(), appSettings.ProcessingBudget,
                    appSettings.WorkerThreads);

                Console.WriteLine(" ?> Processing budget: {0}", budget);

                // Register the application services shared by every job-executing host
                // (incl. IOptions<AppSettings>). Kept in a single extension method so the same
                // container can be built in tests (ProcessingNodeDiCompletenessTests) to catch
                // missing registrations before they fail at runtime on a processing node.
                services.AddProcessingNodeServices(configuration, appSettings);

                services.AddHangfireProvider(appSettings, configuration);
                services.AddBudgetedHangfireServers(budget);

                // Register Hangfire jobs initializer as hosted service
                services.AddHostedService<HangfireJobsHostedService>();
            })
            .Build();

        // Initialize cache providers (required by BuildPendingService and other services)
        using (var scope = host.Services.CreateScope())
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            new CacheInitializer(scope.ServiceProvider, logger).InitializeAsync(CancellationToken.None).Wait();
        }

        Console.WriteLine(" ?> Processing node started");
        Console.WriteLine(" ?> Press Ctrl+C to quit");

        host.Run();
        return ExitCodes.Success;
    }

    private static int RunAsWebApplication(Options opts)
    {
        Console.WriteLine(" ?> Using address '{0}'", opts.Address);
        Console.WriteLine(" -> Starting web host");

        var args = new[] { "--urls", $"http://{opts.Address}" };

        WithInstanceType(CreateHostBuilder(args), opts.InstanceType).Build().Run();
        return ExitCodes.Success;
    }

    /// <summary>
    /// Builds the web host (Startup pipeline + Serilog). Shared by <see cref="RunAsWebApplication"/>
    /// and by the integration tests (WebApplicationFactory resolves this method by convention).
    /// Instance-specific settings (e.g. InstanceType, --urls) are layered on top by the caller.
    /// </summary>
    /// <param name="args">Host arguments (e.g. <c>--urls</c>).</param>
    /// <returns>The configured, not yet built, host builder.</returns>
    public static IHostBuilder CreateHostBuilder(string[] args)
    {
#if DEBUG_EF
        // dotnet-ef resolves this method before Main: route its invocations to the design-time host
        if (IsEfTool(args))
            return CreateEfToolsHostBuilder(args);
#endif

        return Host.CreateDefaultBuilder(args)
            .UseSerilog(ConfigureSerilog)
            .ConfigureWebHostDefaults(webBuilder => webBuilder.UseStartup<Startup>());
    }

    /// <summary>
    /// The one Serilog setup of every host (web server, processing node, thumbnail generator): sinks and
    /// levels come from the <c>Serilog</c> configuration section, so all instances log the same way. The
    /// file sink path gets the instance type (<see cref="InstanceLogPaths"/>): containers sharing the
    /// storage folder never write the same file.
    /// </summary>
    internal static void ConfigureSerilog(HostBuilderContext context, IServiceProvider services,
        LoggerConfiguration configuration) =>
        configuration
            .ReadFrom.Configuration(InstanceLogPaths.Apply(context.Configuration))
            .ReadFrom.Services(services)
            .Enrich.FromLogContext();

    /// <summary>
    /// Fails fast when the libddb on the library path cannot be used (XI-01): it must load, have ABI
    /// 1.x with x >= 1 and export every function Registry calls (<see cref="DdbAbi.Verify"/>). The
    /// library itself is configured and initialized by each host's <c>DdbRuntimeService</c>.
    /// </summary>
    private static bool InitializeDdb()
    {
        try
        {
            Console.WriteLine(" ?> DDB: " + DdbAbi.Verify());
            return true;
        }
        catch (DdbAbiMismatchException e)
        {
            CommonUtils.WriteLineColor($" !> Unable to use the DDB library: {e.Message}", ConsoleColor.Red);
            Console.WriteLine(
                $" ?> Check installation instructions on {MagicStrings.DdbInstallPageUrl} and try again.");
            Console.WriteLine($" ?> Releases page: {MagicStrings.DdbReleasesPageUrl}");

            return false;
        }
    }

    private static bool VerifyOptions(Options opts)
    {
        if (opts.StorageFolder == null)
        {
            CommonUtils.WriteColor(" !> Storage folder is not specified", ConsoleColor.Red);
            return false;
        }

        if (opts.Address == null)
            return true;

        if (opts.Address.Length == 0)
        {
            CommonUtils.WriteColor(" !> Address not specified", ConsoleColor.Red);
            return false;
        }

        var parts = opts.Address.Split(':');

        var host = DefaultHost;
        var port = DefaultPort;

        switch (parts.Length)
        {
            case 1:
                {
                    if (!int.TryParse(parts[0], out port))
                    {
                        // Try to parse as hostname
                        host = parts[0];

                        if (!host.Equals("localhost", StringComparison.InvariantCultureIgnoreCase) &&
                            !IPEndPoint.TryParse(host, out _))
                        {
                            CommonUtils.WriteColor(" !> Invalid address", ConsoleColor.Red);
                            return false;
                        }
                    }

                    if (port is < 1 or > 65535)
                    {
                        CommonUtils.WriteColor(" !> Invalid port", ConsoleColor.Red);
                        return false;
                    }

                    break;
                }
            case 2:
                {
                    host = parts[0];

                    if (!host.Equals("localhost", StringComparison.InvariantCultureIgnoreCase) &&
                        !IPEndPoint.TryParse(host, out _))
                    {
                        CommonUtils.WriteColor(" !> Invalid address", ConsoleColor.Red);
                        return false;
                    }

                    if (!int.TryParse(parts[1], out port))
                    {
                        CommonUtils.WriteColor(" !> Invalid port", ConsoleColor.Red);
                        return false;
                    }

                    if (port is < 1 or > 65535)
                    {
                        CommonUtils.WriteColor(" !> Invalid port", ConsoleColor.Red);
                        return false;
                    }

                    break;
                }
            default:
                CommonUtils.WriteColor(" !> Invalid address", ConsoleColor.Red);
                return false;
        }

        opts.Address = $"{host}:{port}";

        return true;
    }

    private static bool SetupStorageFolder(string folder, bool resetSpa = false, bool resetDdb = false,
        bool deployDdb = false, bool deployHub = true)
    {
        Console.WriteLine(" -> Setting up storage folder");

        Directory.CreateDirectory(folder);

        if (deployHub)
        {
            // The branding folder hosts user-supplied logos / favicons / manifest.
            // It is created (empty) on first run and is NEVER touched by SetupHub(),
            // so customizations survive Hub upgrades.
            Directory.CreateDirectory(Path.Combine(folder, MagicStrings.BrandingFolder));
        }

        if (deployDdb && !SetupDdb(folder, resetDdb))
        {
            CommonUtils.WriteLineColor(" !> Failed to deploy DDB to storage folder", ConsoleColor.Red);
            return false;
        }

        // Only the webserver extracts/upgrades the ClientApp (Hub) folder. Worker nodes
        // (ProcessingNode, ThumbnailGenerator) skip this to avoid racing with the webserver
        // on a shared storage folder during a Hub upgrade.
        if (deployHub && !SetupHub(folder, resetSpa))
        {
            Console.WriteLine(" !> Error while setting up Hub");
            return false;
        }

        var settingsFilePath = Path.Combine(folder, MagicStrings.AppSettingsFileName);

        var defaultSettingsConfig = GetDefaultSettings();

        // The appsettings file does not contain only appsettings
        if (!File.Exists(settingsFilePath))
        {
            Console.WriteLine(" -> Creating default appsettings.json");

            File.WriteAllText(MagicStrings.AppSettingsFileName,
                JsonConvert.SerializeObject(defaultSettingsConfig, Formatting.Indented));
        }

        if (!TryLoadSettings(settingsFilePath, ToAppSettings(defaultSettingsConfig), out var settingsConfig,
                out var settings))
            return false;

        try
        {
            Directory.CreateDirectory(settings.TempPath);

            // Update settings (persists the values VerifySettings filled in, e.g. generated secrets)
            Console.WriteLine(" -> Updating settings");
            settingsConfig["AppSettings"] = JObject.FromObject(settings);
            File.WriteAllText(settingsFilePath, JsonConvert.SerializeObject(settingsConfig, Formatting.Indented));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(" !> Error while updating settings");
            Console.Error.WriteLine(e.Message);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads <paramref name="settingsFilePath"/> and verifies its <c>AppSettings</c> section
    /// (<see cref="VerifySettings"/>; errors on stderr). Read-only: the values verification fills in
    /// (generated secrets, default temp path) are only applied to the returned <paramref name="settings"/>.
    /// </summary>
    private static bool TryLoadSettings(string settingsFilePath, AppSettings defaultSettings,
        out JObject settingsConfig, out AppSettings settings)
    {
        Console.WriteLine(" -> Verifying settings");

        settingsConfig = null;
        settings = null;
        try
        {
            settingsConfig = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(settingsFilePath));
            settings = ToAppSettings(settingsConfig);

            if (VerifySettings(settings, defaultSettings))
                return true;

            Console.Error.WriteLine(" !> Error while verifying settings");
            return false;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(" !> Error while verifying settings");
            Console.Error.WriteLine(e.Message);
            return false;
        }
    }

    private static AppSettings ToAppSettings(JObject settingsConfig) =>
        settingsConfig?["AppSettings"]?.ToObject<AppSettings>();

    /// <summary>
    /// <c>--check</c>: verifies the storage folder's <c>appsettings.json</c> without starting a host
    /// and without writing anything (no default settings, Hub, ddb or temp folder).
    /// </summary>
    /// <returns><see cref="ExitCodes.Success"/> when valid, otherwise <see cref="ExitCodes.Failure"/> (reasons on stderr).</returns>
    private static int CheckConfiguration(string storageFolder)
    {
        if (!TryLoadStorageSettings(storageFolder, out _))
            return ExitCodes.Failure;

        CommonUtils.WriteLineColor(" ?> Configuration is valid", ConsoleColor.Green);
        return ExitCodes.Success;
    }

    /// <summary>
    /// Loads and verifies the settings of an existing storage folder, read-only (shared by the
    /// maintenance commands, which never create a storage folder).
    /// </summary>
    private static bool TryLoadStorageSettings(string storageFolder, out AppSettings settings)
    {
        settings = null;
        var settingsFilePath = Path.Combine(Path.GetFullPath(storageFolder), MagicStrings.AppSettingsFileName);

        Console.WriteLine(" ?> Using settings '{0}'", settingsFilePath);

        if (!File.Exists(settingsFilePath))
        {
            Console.Error.WriteLine($" !> '{settingsFilePath}' not found: run the Registry once on this storage " +
                                    "folder to create it");
            return false;
        }

        return TryLoadSettings(settingsFilePath, ToAppSettings(GetDefaultSettings()), out _, out settings);
    }

    /// <summary>
    /// <c>--reset-admin-password</c>: sets a new password for the configured default admin in the
    /// configured identity database, without starting the web host. The password comes from
    /// <paramref name="readAdminPassword"/> (environment variable or stdin), never from the command line.
    /// </summary>
    private static int ResetAdminPassword(string storageFolder, Func<string> readAdminPassword)
    {
        if (!TryLoadStorageSettings(storageFolder, out _))
            return ExitCodes.Failure;

        string password;
        try
        {
            password = readAdminPassword();
        }
        catch (InvalidOperationException)
        {
            // No terminal to read from (Console.ReadKey without a console)
            password = null;
        }

        if (string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine($" !> No password: set {AdminPasswordEnvironmentVariable} or write it to stdin");
            return ExitCodes.Failure;
        }

        // Relative paths of the settings (e.g. "Data Source=./identity.db") resolve against the storage
        // folder, as when the Registry runs
        storageFolder = Path.GetFullPath(storageFolder);
        Environment.CurrentDirectory = storageFolder;

        try
        {
            // The web host's container (identity store, password policy, user manager), built but not started
            using var host = CreateHostBuilder([]).UseContentRoot(storageFolder).Build();
            return ResetDefaultAdminPasswordAsync(host.Services, password).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($" !> Cannot reset the admin password: {ex.Message}");
            return ExitCodes.Failure;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Resets the password of <c>AppSettings.DefaultAdmin.UserName</c> through the
    /// <see cref="UserManager{TUser}"/> of <paramref name="services"/> (password policy applies, the
    /// security stamp changes so existing sessions end) and clears a lockout.
    /// </summary>
    internal static async Task<int> ResetDefaultAdminPasswordAsync(IServiceProvider services, string newPassword)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        var userName = provider.GetRequiredService<IOptions<AppSettings>>().Value.DefaultAdmin?.UserName;
        if (string.IsNullOrWhiteSpace(userName))
        {
            Console.Error.WriteLine(" !> AppSettings:DefaultAdmin:UserName is not set");
            return ExitCodes.Failure;
        }

        var userManager = provider.GetRequiredService<UserManager<User>>();
        var user = await userManager.FindByNameAsync(userName);
        if (user == null)
        {
            Console.Error.WriteLine($" !> The default admin '{userName}' does not exist in the identity database");
            return ExitCodes.Failure;
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var reset = await userManager.ResetPasswordAsync(user, token, newPassword);
        if (!reset.Succeeded)
        {
            Console.Error.WriteLine($" !> Password not changed: {string.Join(" ", reset.Errors.Select(e => e.Description))}");
            return ExitCodes.Failure;
        }

        await userManager.SetLockoutEndDateAsync(user, null);
        await userManager.ResetAccessFailedCountAsync(user);

        CommonUtils.WriteLineColor($" ?> Password of the default admin '{userName}' changed", ConsoleColor.Green);
        return ExitCodes.Success;
    }

    /// <summary>
    /// The new admin password: <see cref="AdminPasswordEnvironmentVariable"/> when set, otherwise one line
    /// from <paramref name="readLine"/>; <c>null</c> when neither has one.
    /// </summary>
    internal static string ReadAdminPassword(Func<string, string> getEnvironmentVariable, Func<string> readLine)
    {
        var fromEnvironment = getEnvironmentVariable(AdminPasswordEnvironmentVariable);
        if (!string.IsNullOrEmpty(fromEnvironment))
            return fromEnvironment;

        var line = readLine();
        return string.IsNullOrEmpty(line) ? null : line;
    }

    private static string ReadAdminPasswordFromConsole() =>
        ReadAdminPassword(Environment.GetEnvironmentVariable,
            Console.IsInputRedirected ? Console.In.ReadLine : ReadHiddenLine);

    /// <summary>Reads a line from the terminal without echoing it (prompt on stderr).</summary>
    private static string ReadHiddenLine()
    {
        Console.Error.Write(" ?> New password for the default admin: ");

        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                    password.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
                password.Append(key.KeyChar);
        }

        Console.Error.WriteLine();
        return password.ToString();
    }

    private static bool SetupHub(string folder, bool resetSpa)
    {
        var hubRoot = Path.Combine(folder, MagicStrings.SpaRoot);
        var embeddedVersion = ReadEmbeddedHubVersion();
        var onDiskVersion = ReadOnDiskHubVersion(hubRoot);

        bool ok;
        if (resetSpa)
        {
            Console.WriteLine(" -> Resetting Hub (--reset-spa)");
            if (Directory.Exists(hubRoot))
            {
                Console.WriteLine(" -> Cleaning hub folder contents (mountpoint-safe)");
                CommonUtils.EmptyDirectoryContents(hubRoot);
            }
            ok = ExtractHub(hubRoot, embeddedVersion);
        }
        else if (!Directory.Exists(hubRoot) || Directory.GetFiles(hubRoot).Length == 0)
        {
            Console.WriteLine($" -> Installing Hub v{embeddedVersion ?? "unknown"}");
            ok = ExtractHub(hubRoot, embeddedVersion);
        }
        else if (Services.Hub.HubVersionComparer.ShouldUpgrade(onDiskVersion, embeddedVersion))
        {
            Console.WriteLine($" -> Upgrading Hub: {onDiskVersion ?? "unknown"} -> {embeddedVersion}");
            Console.WriteLine(" -> Cleaning hub folder contents (mountpoint-safe)");
            CommonUtils.EmptyDirectoryContents(hubRoot);
            ok = ExtractHub(hubRoot, embeddedVersion);
        }
        else
        {
            Console.WriteLine($" ?> Hub folder is ok (v{onDiskVersion ?? "unknown"})");
            ok = true;
        }

        // Record the version that is actually on disk so /sys/features can
        // expose it to the SPA for client-side update detection.
        Services.Hub.HubInfo.Initialize(ReadOnDiskHubVersion(hubRoot) ?? embeddedVersion);

        return ok;
    }

    /// <summary>
    /// Reads the Hub version stamped inside the embedded ClientApp.zip.
    /// Returns <c>null</c> if the marker file is missing (older ZIPs).
    /// </summary>
    private static string ReadEmbeddedHubVersion()
    {
        try
        {
            var efp = new EmbeddedResourceQuery();
            using var stream = efp.Read(Assembly.GetExecutingAssembly(), MagicStrings.SpaRoot + ".zip");
            if (stream == null) return null;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = zip.GetEntry(MagicStrings.HubVersionFile);
            if (entry == null) return null;
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd().Trim();
        }
        catch (Exception e)
        {
            Console.WriteLine($" !> Failed to read embedded Hub version: {e.Message}");
            return null;
        }
    }

    private static string ReadOnDiskHubVersion(string hubRoot)
    {
        var path = Path.Combine(hubRoot, MagicStrings.HubVersionFile);
        if (!File.Exists(path)) return null;
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch
        {
            return null;
        }
    }

    private static bool SetupDdb(string folder, bool resetDdb)
    {
        var ddbRoot = Path.Combine(folder, MagicStrings.DdbArchive);

        if (resetDdb)
        {
            Console.WriteLine(" -> Resetting ddb installation");
            if (Directory.Exists(ddbRoot)) CommonUtils.EmptyDirectoryContents(ddbRoot);
        }
        else if (
            Directory.Exists(ddbRoot) &&
            Directory.GetFiles(ddbRoot).Length != 0)
        {
            Console.WriteLine(" ?> Ddb folder is ok");

            Environment.SetEnvironmentVariable("PATH",
                $"{ddbRoot}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");

            return true;
        }

        var res = ExtractDdb(ddbRoot);

        if (!res) return false;

        Environment.SetEnvironmentVariable("PATH",
            $"{ddbRoot}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");

        return true;
    }

    private static bool ExtractEmbeddedArchive(string archiveName, string folder)
    {
        var efp = new EmbeddedResourceQuery();
        var executingAssembly = Assembly.GetExecutingAssembly();

        Directory.CreateDirectory(folder);

        Console.WriteLine($" -> Extracting '{archiveName}'");

        // Read embedded resource and extract to storage folder
        using var stream = efp.Read(executingAssembly, archiveName + ".zip");

        if (stream == null)
        {
            Console.WriteLine($" !> Failed to read embedded archive");
            return false;
        }

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        if (zip.Entries.Count == 0)
        {
            Console.WriteLine($" !> Error while extracting '{archiveName}', empty archive");
            return false;
        }

        Console.WriteLine(" ?> Found {0} entries", zip.Entries.Count);

        // overwriteFiles: true is a defensive guard in case a previous cleanup left
        // residual files (e.g. on bind-mounted folders where the mount root cannot
        // be removed via rmdir and is emptied in place).
        zip.ExtractToDirectory(folder, overwriteFiles: true);

        return true;
    }

    private static bool ExtractDdb(string folder)
    {
        return ExtractEmbeddedArchive(MagicStrings.DdbArchive, folder);
    }

    private static bool ExtractHub(string folder, string embeddedVersion = null)
    {
        if (!ExtractEmbeddedArchive(MagicStrings.SpaRoot, folder)) return false;

        // Defensive: ensure the version marker is written even if the embedded
        // ZIP did not carry one (e.g. legacy build outputs).
        if (!string.IsNullOrWhiteSpace(embeddedVersion))
        {
            var versionPath = Path.Combine(folder, MagicStrings.HubVersionFile);
            if (!File.Exists(versionPath))
            {
                try { File.WriteAllText(versionPath, embeddedVersion); }
                catch (Exception e) { Console.WriteLine($" !> Failed to write Hub version marker: {e.Message}"); }
            }
        }

        return true;
    }

    private static JObject GetDefaultSettings()
    {
        var efp = new EmbeddedResourceQuery();
        var executingAssembly = Assembly.GetExecutingAssembly();
        using var reader = new StreamReader(efp.Read(executingAssembly, MagicStrings.AppSettingsDefaultFileName));
        return JsonConvert.DeserializeObject<JObject>(reader.ReadToEnd());
    }

    private static bool VerifySettings(AppSettings settings, AppSettings defaultSettings)
    {
        if (settings == null)
        {
            Console.Error.WriteLine(" !> Empty settings, check appsettings.json");
            return false;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(settings.Secret))
                settings.Secret = CommonUtils.RandomString(64);

            // Shared by the web server and the ThumbnailGenerator host (same appsettings.json)
            if (string.IsNullOrWhiteSpace(settings.ThumbnailGeneratorToken))
                settings.ThumbnailGeneratorToken = CommonUtils.RandomString(64);

            var externalUrl = settings.ExternalUrlOverride;

            if (!string.IsNullOrWhiteSpace(externalUrl) && !Uri.TryCreate(externalUrl, UriKind.Absolute, out _))
            {
                Console.Error.WriteLine(" !> ExternalUrlOverride is not a valid URL");
                return false;
            }

            if (settings.TokenExpirationInDays < 1)
            {
                Console.Error.WriteLine(" !> TokenExpirationInDays is not valid (must be greater than 0)");
                return false;
            }

            var defaultAdmin = settings.DefaultAdmin;

            if (defaultAdmin?.Email == null || defaultAdmin.Password == null || defaultAdmin.UserName == null)
            {
                Console.Error.WriteLine(" !> DefaultAdmin is not valid");
                return false;
            }

            if (string.IsNullOrWhiteSpace(settings.DatasetsPath))
            {
                Console.Error.WriteLine(" !> DatasetsPath is empty");
                return false;
            }

            if (string.IsNullOrWhiteSpace(settings.CachePath))
            {
                Console.Error.WriteLine(" !> CachePath is empty");
                return false;
            }

            if (string.IsNullOrWhiteSpace(settings.TempPath))
            {
                settings.TempPath = defaultSettings.TempPath;
                Console.WriteLine(" ?> TempPath is empty, using default");
            }

            if (settings.BatchTokenLength < 8)
            {
                Console.Error.WriteLine(" !> BatchTokenLength is not valid (must be at least 8)");
                return false;
            }

            if (settings.UploadBatchTimeout.TotalMinutes < 1)
            {
                Console.Error.WriteLine(" !> UploadBatchTimeout is not valid (must be at least 1 minute)");
                return false;
            }

            if (settings.RandomDatasetNameLength < 8)
            {
                Console.Error.WriteLine(" !> RandomDatasetNameLength is not valid (must be at least 8)");
                return false;
            }

            if (string.IsNullOrWhiteSpace(settings.AuthCookieName))
            {
                Console.Error.WriteLine(" !> AuthCookieName is empty");
                return false;
            }

            if (settings.ThumbnailsCacheExpiration.HasValue &&
                settings.ThumbnailsCacheExpiration.Value.TotalMinutes < 1)
            {
                Console.Error.WriteLine(" !> ThumbnailsCacheExpiration is not valid (must be at least 1 minute)");
                return false;
            }

            if (settings.TilesCacheExpiration.HasValue && settings.TilesCacheExpiration.Value.TotalMinutes < 1)
            {
                Console.Error.WriteLine(" !> TilesCacheExpiration is not valid (must be at least 1 minute)");
                return false;
            }

            if (settings.DatasetVisibilityCacheExpiration.HasValue &&
                settings.DatasetVisibilityCacheExpiration.Value.TotalMinutes < 1)
            {
                Console.Error.WriteLine(" !> DatasetVisibilityCacheExpiration is not valid (must be at least 1 minute)");
                return false;
            }

            if (settings.ClearCacheInterval.HasValue && settings.ClearCacheInterval.Value.TotalMinutes < 1)
            {
                Console.Error.WriteLine(" !> ClearCacheInterval is not valid (must be at least 1 minute)");
                return false;
            }

            if (settings.LdapSettings?.Enabled == true)
            {
                if (!string.IsNullOrWhiteSpace(settings.ExternalAuthUrl))
                {
                    Console.Error.WriteLine(" !> Cannot enable both LDAP and External authentication simultaneously");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(settings.LdapSettings.Server))
                {
                    Console.Error.WriteLine(" !> LdapSettings.Server is required when LDAP is enabled");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(settings.LdapSettings.BaseDn))
                {
                    Console.Error.WriteLine(" !> LdapSettings.BaseDn is required when LDAP is enabled");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(settings.LdapSettings.SearchFilter) ||
                     !settings.LdapSettings.SearchFilter.Contains("{0}", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine(" !> LdapSettings.SearchFilter must contain the '{0}' placeholder for the username");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(" !> Exception: " + ex.Message);
            return false;
        }

        return true;
    }

    #region EF Tool

    /*
        Add new migration (RegistryContext)
        dotnet ef migrations add NewMigration --project Registry.Web.Data.SqliteMigrations --context Registry.Web.Data.RegistryContext --configuration DebugEF --startup-project Registry.Web --verbose -- --provider Sqlite
        dotnet ef migrations add NewMigration --project Registry.Web.Data.MysqlMigrations  --context Registry.Web.Data.RegistryContext --configuration DebugEF --startup-project Registry.Web --verbose -- --provider MySql

        Generate SQL script (RegistryContext)
        dotnet ef migrations script --project Registry.Web.Data.SqliteMigrations --context Registry.Web.Data.RegistryContext --configuration DebugEF --startup-project Registry.Web -o sqlite.sql --verbose -- --provider Sqlite
        dotnet ef migrations script --project Registry.Web.Data.MysqlMigrations  --context Registry.Web.Data.RegistryContext --configuration DebugEF --startup-project Registry.Web -o mysql.sql --verbose -- --provider MySql

        Add new migration (ApplicationDbContext)
        dotnet ef migrations add NewMigration --project Registry.Web.Identity.SqliteMigrations --context Registry.Web.Identity.ApplicationDbContext --configuration DebugEF --startup-project Registry.Web --verbose -- --provider Sqlite
        dotnet ef migrations add NewMigration --project Registry.Web.Identity.MysqlMigrations  --context Registry.Web.Identity.ApplicationDbContext --configuration DebugEF --startup-project Registry.Web --verbose -- --provider MySql

        Generate SQL script (ApplicationDbContext)
        dotnet ef migrations script --project Registry.Web.Identity.SqliteMigrations --context Registry.Web.Identity.ApplicationDbContext --configuration DebugEF --startup-project Registry.Web -o sqlite.sql --verbose -- --provider Sqlite
        dotnet ef migrations script --project Registry.Web.Identity.MysqlMigrations  --context Registry.Web.Identity.ApplicationDbContext --configuration DebugEF --startup-project Registry.Web -o mysql.sql --verbose -- --provider MySql

    */

    private const string EfProviderOption = "--provider";

    /// <summary>
    /// Reads the provider of an EF Core tools invocation (<c>dotnet ef ... -- --provider MySql</c>).
    /// dotnet-ef hands the arguments after <c>--</c> to the startup assembly in no fixed order and
    /// may add others (e.g. <c>--applicationName</c>, as one <c>--name=value</c> token or two), so
    /// the option is looked up anywhere, in both the <c>--provider value</c> and
    /// <c>--provider=value</c> forms. The application's own command line has no such option.
    /// </summary>
    /// <param name="args">The arguments the host receives.</param>
    /// <param name="provider">The provider name (e.g. <c>Sqlite</c>, <c>MySql</c>) when found.</param>
    /// <returns>True when the arguments carry a provider, i.e. they come from the EF Core tools.</returns>
    internal static bool TryGetEfToolProvider(IReadOnlyList<string> args, out string provider)
    {
        provider = null;
        if (args == null)
            return false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == null)
                continue;

            if (arg.Equals(EfProviderOption, StringComparison.OrdinalIgnoreCase))
            {
                var value = i + 1 < args.Count ? args[i + 1] : null;
                if (!string.IsNullOrWhiteSpace(value) && !value.StartsWith("--", StringComparison.Ordinal))
                    provider = value;
            }
            else if (arg.StartsWith(EfProviderOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg[(EfProviderOption.Length + 1)..];
                if (!string.IsNullOrWhiteSpace(value))
                    provider = value;
            }
        }

        return provider != null;
    }

#if DEBUG_EF
        private static bool IsEfTool(string[] args) => TryGetEfToolProvider(args, out _);

        private static void RunEfToolsHost(string[] args) => CreateEfToolsHostBuilder(args).Build().Run();

        /// <summary>
        /// The design-time host for the EF Core tools: only the two DbContexts, on the provider
        /// passed with <c>--provider</c> (<see cref="TryGetEfToolProvider"/>), default Sqlite.
        /// </summary>
        private static IHostBuilder CreateEfToolsHostBuilder(string[] args)
        {
            var provider = TryGetEfToolProvider(args, out var requested) ? requested : "Sqlite";

            return Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration(x => x.AddJsonFile("appsettings-eftools.json"))
                .ConfigureServices(
                    (hostContext, services) =>
                    {
                        var configuration = hostContext.Configuration;

                        var registrySqliteConnectionString =
                            configuration.GetConnectionString("RegistrySqliteConnection");
                        var registryMysqlConnectionString =
                            configuration.GetConnectionString("RegistryMysqlConnection");

                        // One probe per connection string for the whole host, or none when pinned
                        var mySqlVersions = MySqlServerVersionCache.FromConfiguration(configuration);

                        services.AddDbContext<RegistryContext>(
                            options => _ = provider switch
                            {
                                "Sqlite" => options.UseSqlite(
                                    registrySqliteConnectionString,
                                    x => x.MigrationsAssembly("Registry.Web.Data.SqliteMigrations")),

                                "MySql" => options.UseMySql(
                                    registryMysqlConnectionString, mySqlVersions.Resolve(registryMysqlConnectionString),
                                    x => x.MigrationsAssembly("Registry.Web.Data.MySqlMigrations")),

                                _ => throw new Exception($"Unsupported provider: {provider}")
                            });

                        var identitySqliteConnectionString =
                            configuration.GetConnectionString("IdentitySqliteConnection");
                        var identityMysqlConnectionString =
                            configuration.GetConnectionString("IdentityMysqlConnection");

                        services.AddDbContext<ApplicationDbContext>(
                            options => _ = provider switch
                            {
                                "Sqlite" => options.UseSqlite(
                                    identitySqliteConnectionString,
                                    x => x.MigrationsAssembly("Registry.Web.Identity.SqliteMigrations")),

                                "MySql" => options.UseMySql(
                                    identityMysqlConnectionString, mySqlVersions.Resolve(identityMysqlConnectionString),
                                    x => x.MigrationsAssembly("Registry.Web.Identity.MySqlMigrations")),

                                _ => throw new Exception($"Unsupported provider: {provider}")
                            });
                    });
        }
#endif

    #endregion
}
