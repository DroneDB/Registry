#nullable disable
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using static Registry.Web.Services.Configuration.ConfigurationFields;

namespace Registry.Web.Services.Configuration;

/// <summary>The admin configuration sections of the host: connection strings, logging and the remaining settings.</summary>
public sealed class HostSections : IConfigurationSectionBuilder
{
    private readonly ConfigurationView _view;
    private readonly IConfiguration _configuration;

    public HostSections(ConfigurationView view, IConfiguration configuration)
    {
        _view = view;
        _configuration = configuration;
    }

    public IEnumerable<PlacedSection> Build() =>
    [
        new(ConfigurationSection.ConnectionStrings, BuildConnectionStringsSection()),
        new(ConfigurationSection.Serilog, BuildSerilogSection()),
        new(ConfigurationSection.Misc, BuildMiscSection())
    ];

    private ConfigurationSectionDataDto BuildConnectionStringsSection()
    {
        // Connection strings are at the top level of appsettings.json, not under AppSettings.
        // Read current values from IConfiguration and defaults from embedded resource.
        var currentIdentity = _configuration.GetConnectionString("IdentityConnection")
            ?? "Data Source=./identity.db;Mode=ReadWriteCreate";
        var currentRegistry = _configuration.GetConnectionString("RegistryConnection")
            ?? "Data Source=./registry.db;Mode=ReadWriteCreate";
        var defaultIdentity = "Data Source=./identity.db;Mode=ReadWriteCreate";
        var defaultRegistry = "Data Source=./registry.db;Mode=ReadWriteCreate";

        return Section("ConnectionStrings", "Connection Strings",
            "Database connection strings for Identity and Registry databases.",
            [
                SensitiveField("IdentityConnection", "Identity Connection String",
                    "Connection string for the Identity (auth) database.",
                    currentIdentity, defaultIdentity),

                SensitiveField("RegistryConnection", "Registry Connection String",
                    "Connection string for the main Registry database.",
                    currentRegistry, defaultRegistry)
            ]);
    }

    private static ConfigurationSectionDataDto BuildSerilogSection()
    {
        // Serilog config is at the top level of appsettings.json, not under AppSettings.
        // We provide the default values from appsettings-default.json.
        var defaultLogPath = "./logs/registry.txt";
        var defaultMinLevel = "Warning";

        return Section("Serilog", "Serilog Logging",
            "Logging configuration: minimum level and file output path.",
            [
                StringField("MinimumLevel", "Minimum Log Level",
                    "Minimum severity level for log output. Values: Verbose, Debug, Information, Warning, Error, Fatal.",
                    defaultMinLevel, defaultMinLevel),

                StringField("LogPath", "Log File Path",
                    "Path to the log file output. Each instance type writes its own file: the web server, " +
                    "processing node and thumbnail generator add -web, -node and -thumbs to the file name " +
                    "(e.g. registry-web.txt); the default all-in-one instance uses it as is. Files roll daily.",
                    defaultLogPath, defaultLogPath)
            ]);
    }

    private ConfigurationSectionDataDto BuildMiscSection()
    {
        var account = _view.Configured<AccountOptions>();
        var accountDefault = _view.Shipped<AccountOptions>();
        var host = _view.Configured<HostResourceOptions>();
        var hostDefault = _view.Shipped<HostResourceOptions>();
        var http = _view.Configured<HttpHostOptions>();
        var httpDefault = _view.Shipped<HttpHostOptions>();
        var share = _view.Configured<ShareOptions>();
        var shareDefault = _view.Shipped<ShareOptions>();
        var storage = _view.Configured<StorageOptions>();
        var storageDefault = _view.Shipped<StorageOptions>();
        var token = _view.Configured<AuthTokenOptions>();
        var tokenDefault = _view.Shipped<AuthTokenOptions>();
        return Section("Misc", "Miscellaneous",
            "Worker threads, CORS origins, external URL override, random name length, and data protection keys.",
            [
                NumberField("WorkerThreads", "Worker Threads",
                    "Number of worker threads. 0 or -1 uses ASP.NET default.",
                    host.WorkerThreads, hostDefault.WorkerThreads),

                ArrayField("AllowedOrigins", "Allowed CORS Origins",
                    "Allowed CORS origins. Null or empty = all origins allowed.",
                    http.AllowedOrigins, httpDefault.AllowedOrigins),

                ArrayField("KnownProxies", "Known Proxies",
                    "IP addresses of the reverse proxies whose X-Forwarded-For/X-Forwarded-Proto headers are trusted. Null or empty = loopback (127.0.0.1, ::1). When set, it replaces loopback.",
                    http.KnownProxies, httpDefault.KnownProxies),

                ArrayField("KnownNetworks", "Known Networks",
                    "Networks (CIDR, e.g. 172.16.0.0/12) whose addresses are trusted as reverse proxies, in addition to Known Proxies. Needed behind a port published by Docker, where the peer is the bridge gateway. Null or empty = none.",
                    http.KnownNetworks, httpDefault.KnownNetworks),

                StringField("ExternalUrlOverride", "External URL Override",
                    "Overrides the external URL used in generated links and redirects.",
                    http.ExternalUrlOverride, httpDefault.ExternalUrlOverride),

                NumberField("RandomDatasetNameLength", "Random Dataset Name Length",
                    "Length of randomly generated dataset names. Must be at least 8.",
                    share.RandomDatasetNameLength, shareDefault.RandomDatasetNameLength,
                    minValue: 8),

                BoolField("EnableStorageLimiter", "Enable Storage Limiter",
                    "Enables per-user storage quota enforcement.",
                    account.EnableStorageLimiter, accountDefault.EnableStorageLimiter),

                StringField("DataProtectionKeysPath", "Data Protection Keys Path",
                    "Path to shared directory for ASP.NET Core Data Protection keys. Required when processing node runs separately without Redis.",
                    storage.DataProtectionKeysPath, storageDefault.DataProtectionKeysPath),

                SensitiveField("MonitorToken", "Monitor Token",
                    "Token for health check endpoints (/health, /quickhealth).",
                    token.MonitorToken, tokenDefault.MonitorToken)
            ]);
    }
}
