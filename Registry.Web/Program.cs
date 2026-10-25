using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using CommandLine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Web.Services.Adapters;
using Registry.Web.Data;
using Registry.Web.Hosting;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities;
using Serilog;

namespace Registry.Web;

/// <summary>
/// The command line: parses it, prepares the storage folder (Hub, ddb, settings file) and runs the host of
/// the instance type (<see cref="RegistryHost"/>), or a maintenance command. Every message goes through
/// Serilog: a bootstrap logger until a host replaces it.
/// </summary>
public class Program
{
    private const int DefaultPort = 5000;
    private const string DefaultHost = "localhost";

    /// <summary>Configuration key holding the <see cref="InstanceType"/> of the running host.</summary>
    internal const string InstanceTypeKey = "InstanceType";

    /// <summary>Environment variable <c>--reset-admin-password</c> reads the new password from (else stdin).</summary>
    public const string AdminPasswordEnvironmentVariable = "REGISTRY_ADMIN_PASSWORD";

    private static ILogger StartupLog => RegistryHost.StartupLog;

    /// <summary>Entry point; the process exit code is one of <see cref="ExitCodes"/>.</summary>
    public static int Main(string[] args)
    {
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
        RegistryHost.UseBootstrapLogger();

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
            StartupLog.Warning("ddb library not found in PATH");
            return false;
        }

        StartupLog.Information("Found the ddb library in {DdbFolder}", ddbFolder);

        Environment.SetEnvironmentVariable("PATH",
            $"{ddbFolder}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");

        return true;
    }

    private static int RunOptions(Options opts, Func<Options, int> startInstance, Func<string> readAdminPassword)
    {
        var appVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;

        StartupLog.Information("{Application} v{Version} ({Build})", AppDomain.CurrentDomain.FriendlyName, appVersion,
#if DEBUG
            "DEBUG");
#else
            "RELEASE");
#endif

        if (!VerifyOptions(opts)) return ExitCodes.InvalidArguments;

        // Maintenance commands: no host, no storage folder setup
        if (opts.CheckConfig)
            return CheckConfiguration(opts.StorageFolder, opts.InstanceType);

        if (opts.ResetAdminPassword)
            return ResetAdminPassword(opts.StorageFolder, readAdminPassword);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            StartupLog.Information("Exiting...");
        };

        // If we need to initialize ddb from the storage folder (only on windows the embedded ddb is used)
        var lateDdbInit = opts.ResetDdb || (OperatingSystem.IsWindows() && !FixPath());

        opts.StorageFolder = Path.GetFullPath(opts.StorageFolder);
        Directory.CreateDirectory(opts.StorageFolder);
        Environment.CurrentDirectory = opts.StorageFolder;

        StartupLog.Information("Using storage folder {StorageFolder}", opts.StorageFolder);

        // Only the webserver owns the Hub (ClientApp) folder. Processing nodes and
        // thumbnail generators share the same StorageFolder in production deployments,
        // so letting them extract/upgrade ClientApp.zip would race against the webserver
        // (Directory.Delete + ZipArchive.ExtractToDirectory concurrently on the same path).
        var deployHub = opts.InstanceType is InstanceType.Default or InstanceType.WebServer;

        var ready = lateDdbInit
            ? SetupStorageFolder(opts.StorageFolder, opts.ResetHub, opts.ResetDdb, true, deployHub) && InitializeDdb()
            : InitializeDdb() && SetupStorageFolder(opts.StorageFolder, opts.ResetHub, deployHub: deployHub);

        return ready ? RunInstance(opts, startInstance) : ExitCodes.Failure;
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
            StartupLog.Fatal(ex, "Host terminated unexpectedly: {Message}", ex.Message);
            return ExitCodes.Failure;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>Builds the host of <see cref="Options.InstanceType"/> and blocks until it shuts down.</summary>
    private static int StartInstance(Options opts)
    {
        // The processing node has no web server
        string[] args = opts.InstanceType == InstanceType.ProcessingNode ? [] : ["--urls", $"http://{opts.Address}"];

        StartupLog.Information("Starting {InstanceType}{Address}", opts.InstanceType,
            args.Length == 0 ? "" : $" on {opts.Address}");

        using var host = RegistryHost.CreateBuilderFor(opts.InstanceType, args).Build();
        host.Run();
        return ExitCodes.Success;
    }

    /// <summary>
    /// The web host builder (<see cref="RegistryHost.CreateWebBuilder"/>; the instance type comes from the
    /// configuration). Used by the integration tests (WebApplicationFactory resolves this method by
    /// convention) and <c>--reset-admin-password</c>.
    /// </summary>
    /// <param name="args">Host arguments (e.g. <c>--urls</c>).</param>
    /// <returns>The configured, not yet built, host builder.</returns>
    public static IHostBuilder CreateHostBuilder(string[] args)
    {
        return RegistryHost.CreateWebBuilder(null, args);
    }

    /// <summary>
    /// Fails fast when the libddb on the library path cannot be used: it must load, be libddb
    /// 2.x (ABI major 2) and export every function Registry calls (<see cref="DdbAbi.Verify"/>). The
    /// library itself is configured and initialized by each host's <c>DdbRuntimeService</c>.
    /// </summary>
    private static bool InitializeDdb()
    {
        try
        {
            StartupLog.Information("DDB: {DdbAbi}", DdbAbi.Verify());
            return true;
        }
        catch (DdbAbiMismatchException e)
        {
            StartupLog.Error("Unable to use the DDB library: {Message}. Check the installation instructions on " +
                             "{InstallPage} and try again; releases: {ReleasesPage}", e.Message,
                MagicStrings.DdbInstallPageUrl, MagicStrings.DdbReleasesPageUrl);
            return false;
        }
    }

    private static bool VerifyOptions(Options opts)
    {
        if (opts.StorageFolder == null)
            return Invalid("Storage folder is not specified");

        if (!Enum.IsDefined(opts.InstanceType))
            return Invalid("Invalid instance type");

        if (opts.Address == null)
            return true;

        if (opts.Address.Length == 0)
            return Invalid("Address not specified");

        if (!TryNormalizeAddress(opts.Address, out var normalized, out var error))
            return Invalid(error);

        opts.Address = normalized;

        return true;

        static bool Invalid(string message)
        {
            StartupLog.Error("{Error}", message);
            return false;
        }
    }

    /// <summary>
    /// Normalizes a listen address to "host:port". A bare port keeps the default host, a bare host keeps
    /// the default port, and a host or port that is not valid is refused with the reason in <paramref name="error"/>.
    /// </summary>
    internal static bool TryNormalizeAddress(string address, out string normalized, out string error)
    {
        normalized = null;
        error = null;

        var parts = address.Split(':');

        var host = DefaultHost;
        var port = DefaultPort;

        switch (parts.Length)
        {
            case 1:
                // A bare port, or else a bare host
                if (int.TryParse(parts[0], out var barePort))
                    port = barePort;
                else
                    host = parts[0];
                break;
            case 2:
                host = parts[0];
                if (!int.TryParse(parts[1], out port))
                    port = 0;
                break;
            default:
                error = "Invalid address";
                return false;
        }

        if (!IsLocalhostOrAddress(host))
        {
            error = "Invalid address";
            return false;
        }

        if (port is < 1 or > 65535)
        {
            error = "Invalid port";
            return false;
        }

        normalized = $"{host}:{port}";
        return true;

        static bool IsLocalhostOrAddress(string host) =>
            host.Equals("localhost", StringComparison.InvariantCultureIgnoreCase) || IPEndPoint.TryParse(host, out _);
    }

    private static bool SetupStorageFolder(string folder, bool resetSpa = false, bool resetDdb = false,
        bool deployDdb = false, bool deployHub = true)
    {
        StartupLog.Information("Setting up storage folder");

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
            StartupLog.Error("Failed to deploy DDB to storage folder");
            return false;
        }

        // Only the webserver extracts/upgrades the ClientApp (Hub) folder. Worker nodes
        // (ProcessingNode, ThumbnailGenerator) skip this to avoid racing with the webserver
        // on a shared storage folder during a Hub upgrade.
        if (deployHub && !SetupHub(folder, resetSpa))
        {
            StartupLog.Error("Error while setting up Hub");
            return false;
        }

        try
        {
            var changes = SettingsFile.Prepare(folder);

            if (changes.Created)
                StartupLog.Information("Created the default settings {SettingsFile}", changes.Path);
            if (changes.GeneratedSecrets.Count > 0)
                StartupLog.Information("Generated {Secrets} in {SettingsFile}{InPlace}",
                    string.Join(", ", changes.GeneratedSecrets), changes.Path,
                    changes.Atomic ? "" : " (a mount point: written in place)");

            // The folder libddb's cache and the scratch files live in; the hosts check the settings
            Directory.CreateDirectory(SettingsFile.LoadConfiguration(folder).GetRegistryOptions<StorageOptions>().TempPath!);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            StartupLog.Error(e, "Error while preparing the settings file: {Message}", e.Message);
            return false;
        }

        return true;
    }

    /// <summary>
    /// <c>--check</c>: validates the storage folder's configuration (its <c>appsettings.json</c> and the
    /// environment, as a host reads them) with the rules the host runs at start, for
    /// <paramref name="role"/>, without starting a host and without writing anything.
    /// </summary>
    /// <returns><see cref="ExitCodes.Success"/> when valid, otherwise <see cref="ExitCodes.Failure"/> (reasons on stderr).</returns>
    private static int CheckConfiguration(string storageFolder, InstanceType role)
    {
        using var log = RegistryHost.CreateCommandLineLogger();

        if (!TryLoadStorageConfiguration(storageFolder, role, log))
            return ExitCodes.Failure;

        log.Information("Configuration is valid");
        return ExitCodes.Success;
    }

    /// <summary>
    /// Loads and validates the configuration of an existing storage folder, read-only (shared by the
    /// maintenance commands, which never create a storage folder).
    /// </summary>
    private static bool TryLoadStorageConfiguration(string storageFolder, InstanceType role, Serilog.Core.Logger log)
    {
        log.Information("Using settings {SettingsFile}", SettingsFile.PathIn(storageFolder));

        IConfiguration configuration;
        try
        {
            configuration = SettingsFile.LoadConfiguration(storageFolder);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            log.Error("Cannot read the settings: {Message}", e.Message);
            return false;
        }

        var errors = RegistryOptions.Validate(configuration, role);
        foreach (var error in errors)
            log.Error("Invalid setting: {Error}", error);

        return errors.Count == 0;
    }

    /// <summary>
    /// <c>--reset-admin-password</c>: sets a new password for the configured default admin in the
    /// configured identity database, without starting the web host. The password comes from
    /// <paramref name="readAdminPassword"/> (environment variable or stdin), never from the command line.
    /// </summary>
    private static int ResetAdminPassword(string storageFolder, Func<string> readAdminPassword)
    {
        using var log = RegistryHost.CreateCommandLineLogger();

        if (!TryLoadStorageConfiguration(storageFolder, InstanceType.WebServer, log))
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
            log.Error("No password: set {Variable} or write it to stdin", AdminPasswordEnvironmentVariable);
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
            return ResetDefaultAdminPasswordAsync(host.Services, password, log).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Cannot reset the admin password: {Message}", ex.Message);
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
    /// <param name="services">The web host's container.</param>
    /// <param name="newPassword">The new password.</param>
    /// <param name="log">Where the result goes (the command line's logger).</param>
    internal static async Task<int> ResetDefaultAdminPasswordAsync(IServiceProvider services, string newPassword,
        ILogger log)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        var userName = provider.GetRequiredService<IOptions<IdentityProviderOptions>>().Value.DefaultAdmin?.UserName;
        if (string.IsNullOrWhiteSpace(userName))
        {
            log.Error("AppSettings:DefaultAdmin:UserName is not set");
            return ExitCodes.Failure;
        }

        var userManager = provider.GetRequiredService<UserManager<User>>();
        var user = await userManager.FindByNameAsync(userName);
        if (user == null)
        {
            log.Error("The default admin '{UserName}' does not exist in the identity database", userName);
            return ExitCodes.Failure;
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var reset = await userManager.ResetPasswordAsync(user, token, newPassword);
        if (!reset.Succeeded)
        {
            log.Error("Password not changed: {Errors}", string.Join(" ", reset.Errors.Select(e => e.Description)));
            return ExitCodes.Failure;
        }

        await userManager.SetLockoutEndDateAsync(user, null);
        await userManager.ResetAccessFailedCountAsync(user);

        log.Information("Password of the default admin '{UserName}' changed", userName);
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

    /// <summary>Reads a line from the terminal without echoing it (an interactive prompt on stderr, not a log line).</summary>
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
            StartupLog.Information("Resetting Hub (--reset-spa)");
            if (Directory.Exists(hubRoot))
            {
                StartupLog.Information("Cleaning hub folder contents (mountpoint-safe)");
                CommonUtils.EmptyDirectoryContents(hubRoot);
            }
            ok = ExtractHub(hubRoot, embeddedVersion);
        }
        else if (!Directory.Exists(hubRoot) || Directory.GetFiles(hubRoot).Length == 0)
        {
            StartupLog.Information("Installing Hub v{HubVersion}", embeddedVersion ?? "unknown");
            ok = ExtractHub(hubRoot, embeddedVersion);
        }
        else if (Services.Hub.HubVersionComparer.ShouldUpgrade(onDiskVersion, embeddedVersion))
        {
            StartupLog.Information("Upgrading Hub: {OnDisk} -> {Embedded}; cleaning hub folder contents (mountpoint-safe)",
                onDiskVersion ?? "unknown", embeddedVersion);
            CommonUtils.EmptyDirectoryContents(hubRoot);
            ok = ExtractHub(hubRoot, embeddedVersion);
        }
        else
        {
            StartupLog.Information("Hub folder is ok (v{HubVersion})", onDiskVersion ?? "unknown");
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
            StartupLog.Warning("Failed to read embedded Hub version: {Message}", e.Message);
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
            StartupLog.Information("Resetting ddb installation");
            if (Directory.Exists(ddbRoot)) CommonUtils.EmptyDirectoryContents(ddbRoot);
        }
        else if (Directory.Exists(ddbRoot) && Directory.GetFiles(ddbRoot).Length != 0)
        {
            StartupLog.Information("Ddb folder is ok");
            AddToPath(ddbRoot);
            return true;
        }

        if (!ExtractEmbeddedArchive(MagicStrings.DdbArchive, ddbRoot))
            return false;

        AddToPath(ddbRoot);
        return true;

        static void AddToPath(string ddbRoot) =>
            Environment.SetEnvironmentVariable("PATH",
                $"{ddbRoot}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");
    }

    private static bool ExtractEmbeddedArchive(string archiveName, string folder)
    {
        var efp = new EmbeddedResourceQuery();
        var executingAssembly = Assembly.GetExecutingAssembly();

        Directory.CreateDirectory(folder);

        StartupLog.Information("Extracting {Archive}", archiveName);

        // Read embedded resource and extract to storage folder
        using var stream = efp.Read(executingAssembly, archiveName + ".zip");

        if (stream == null)
        {
            StartupLog.Error("Failed to read the embedded archive {Archive}", archiveName);
            return false;
        }

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        if (zip.Entries.Count == 0)
        {
            StartupLog.Error("Error while extracting {Archive}: empty archive", archiveName);
            return false;
        }

        StartupLog.Information("Found {Count} entries", zip.Entries.Count);

        // overwriteFiles: true is a defensive guard in case a previous cleanup left
        // residual files (e.g. on bind-mounted folders where the mount root cannot
        // be removed via rmdir and is emptied in place).
        zip.ExtractToDirectory(folder, overwriteFiles: true);

        return true;
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
                catch (Exception e) { StartupLog.Warning("Failed to write Hub version marker: {Message}", e.Message); }
            }
        }

        return true;
    }
}
