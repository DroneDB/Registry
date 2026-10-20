using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using Registry.Common;
using Registry.Ports;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Serilog;

namespace Registry.Web.Test.Support;

/// <summary>
/// Boots the real Registry web host (<see cref="Program.CreateHostBuilder"/> + <see cref="Startup"/>)
/// in-process on a TestServer, so tests exercise the full HTTP pipeline (middlewares, auth, filters).
///
/// Isolation: every instance owns a temp root holding storage/datasets/cache/temp folders and the
/// SQLite identity/registry databases; Hangfire runs in memory as a web-only instance (no servers).
/// Configuration = the embedded <c>appsettings-default.json</c> (what production starts from) plus
/// the test overrides; the developer's <c>Registry.Web/appsettings.json</c> is never loaded.
///
/// Per-test customisation:
/// <list type="bullet">
/// <item>settings: pass <c>settings</c> (configuration keys, e.g. <c>"AppSettings:MonitorToken"</c>);</item>
/// <item>services: pass <c>configureServices</c> (runs after Startup, like ConfigureTestServices);</item>
/// <item>native ddb: <see cref="DdbWrapper"/> is the resolved <see cref="IDdbWrapper"/> (loose mock).</item>
/// </list>
/// Identity: <see cref="TestAuthHandler"/> (header <c>X-Test-User</c>) and peer address
/// <see cref="TestPeerIpStartupFilter"/> (header <c>X-Test-Peer-IP</c>); see
/// <see cref="TestRequestExtensions"/>.
/// </summary>
public sealed class RegistryWebFactory : WebApplicationFactory<Program>
{
    public const string AdminUserName = "admin";
    public const string AdminPassword = "password123";
    public const string AdminEmail = "admin@example.com";
    public const string FakeDdbVersion = "1.12.2";

    private const string DefaultSettingsResourceSuffix = "appsettings-default.json";

    private readonly Dictionary<string, string> _settings;
    private readonly Action<IServiceCollection> _configureServices;

    // Process-wide statics the web host mutates; restored on disposal so sibling fixtures are unaffected
    private readonly JobStorage _savedJobStorage;
    private readonly HashSet<object> _baselineJobFilters;
    private readonly ILogger _savedLogger;
    private bool _cleanedUp;

    public RegistryWebFactory(IDictionary<string, string> settings = null,
        Action<IServiceCollection> configureServices = null)
    {
        RootPath = Path.Combine(Path.GetTempPath(), "registry-web-factory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);

        _settings = CreateDefaultSettings(RootPath);
        if (settings != null)
            foreach (var (key, value) in settings)
                _settings[key] = value;

        _configureServices = configureServices;

        DdbWrapper = new Mock<IDdbWrapper>(MockBehavior.Loose) { DefaultValue = DefaultValue.Empty };
        DdbWrapper.Setup(d => d.GetVersion()).Returns(FakeDdbVersion);

        _savedJobStorage = CurrentJobStorageOrNull();
        _baselineJobFilters = GlobalJobFilters.Filters.Select(f => f.Instance).ToHashSet();
        _savedLogger = Log.Logger;
    }

    /// <summary>Temp root owned by this factory (deleted on disposal).</summary>
    public string RootPath { get; }

    public string StoragePath => _settings["AppSettings:StoragePath"];
    public string DatasetsPath => _settings["AppSettings:DatasetsPath"];

    /// <summary>The <see cref="IDdbWrapper"/> registered in the container (replaces the native one).</summary>
    public Mock<IDdbWrapper> DdbWrapper { get; }

    /// <summary>
    /// Creates a local user (optionally in the admin role) so it can be used with
    /// <see cref="TestRequestExtensions.AsUser"/>.
    /// </summary>
    public async Task<User> CreateUserAsync(string userName, bool isAdmin = false,
        string password = "Password1234")
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        var user = new User { UserName = userName, Email = $"{userName}@example.com" };
        var created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded)
            throw new InvalidOperationException(
                $"Cannot create test user '{userName}': {string.Join(", ", created.Errors.Select(e => e.Description))}");

        if (isAdmin)
            await userManager.AddToRoleAsync(user, ApplicationDbContext.AdminRoleName);

        return user;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Production pipeline (no developer exception page / scope validation differences)
        builder.UseEnvironment("Production");

        // Keep the developer's Registry.Web/appsettings.json (and any ClientApp build) out of the host
        builder.UseContentRoot(RootPath);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddJsonStream(new MemoryStream(ReadDefaultSettings()));
            config.AddInMemoryCollection(_settings);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDdbWrapper>();
            services.AddSingleton(DdbWrapper.Object);

            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName,
                    options => options.ForwardDefaultSelector = TestAuthHandler.ForwardSelector);
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = TestAuthHandler.SchemeName;
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            });

            // First registered = outermost: runs before every other startup filter and Startup.Configure
            services.Insert(0, ServiceDescriptor.Transient<IStartupFilter, TestPeerIpStartupFilter>());

            _configureServices?.Invoke(services);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // The host (re)registers Hangfire.Console while it boots: allow it in every factory
        HangfireConsoleRegistration.Reset();
        return base.CreateHost(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        // Reentrant: base.Dispose(true) calls DisposeAsync, which calls Dispose(true) again
        if (!disposing || _cleanedUp)
            return;
        _cleanedUp = true;

        RestoreStatics();

        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, true);
        }
        catch (IOException)
        {
            // Best effort: a lingering handle must not fail the test run; the folder lives in temp
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above
        }
    }

    private void RestoreStatics()
    {
        // The host registers Hangfire global filters holding its (now disposed) service provider
        foreach (var filter in GlobalJobFilters.Filters.Select(f => f.Instance)
                     .Where(i => !_baselineJobFilters.Contains(i)).ToList())
            GlobalJobFilters.Filters.Remove(filter);

        // Same contract as JobStorageScope: the static cannot be unset, restore only a real value
        if (_savedJobStorage != null)
            JobStorage.Current = _savedJobStorage;

        Log.Logger = _savedLogger;
    }

    private static JobStorage CurrentJobStorageOrNull()
    {
        try
        {
            return JobStorage.Current;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static byte[] ReadDefaultSettings()
    {
        var assembly = typeof(Program).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(DefaultSettingsResourceSuffix, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static Dictionary<string, string> CreateDefaultSettings(string root)
    {
        string Sub(string name) => Path.Combine(root, name);

        return new Dictionary<string, string>
        {
            ["InstanceType"] = nameof(InstanceType.WebServer),
            ["AppSettings:Secret"] = CommonUtils.RandomString(64),
            ["AppSettings:AuthProvider"] = "Sqlite",
            ["AppSettings:RegistryProvider"] = "Sqlite",
            ["AppSettings:HangfireProvider"] = "InMemory",
            ["AppSettings:StoragePath"] = Sub("data"),
            ["AppSettings:DatasetsPath"] = Sub("datasets"),
            ["AppSettings:CachePath"] = Sub("cache"),
            ["AppSettings:TempPath"] = Sub("temp"),
            ["AppSettings:DataProtectionKeysPath"] = Sub("dp-keys"),
            ["AppSettings:DefaultAdmin:Email"] = AdminEmail,
            ["AppSettings:DefaultAdmin:UserName"] = AdminUserName,
            ["AppSettings:DefaultAdmin:Password"] = AdminPassword,
            ["ConnectionStrings:IdentityConnection"] = $"Data Source={Sub("identity.db")};Mode=ReadWriteCreate",
            ["ConnectionStrings:RegistryConnection"] = $"Data Source={Sub("registry.db")};Mode=ReadWriteCreate",
            // The default Serilog file sink writes to ./logs (the test bin folder): keep it in the temp root
            ["Serilog:WriteTo:1:Args:path"] = Path.Combine(root, "logs", "registry.txt")
        };
    }
}
