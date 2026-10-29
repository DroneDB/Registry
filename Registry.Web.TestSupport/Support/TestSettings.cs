using System;
using System.IO;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Registry.Web.Hosting;
using Registry.Web.Models.Configuration;
using Registry.Web.Data;

namespace Registry.Web.Test.Support;

/// <summary>
/// <see cref="AppSettings"/> for tests, replacing per-fixture JSON blobs and
/// <c>Mock&lt;IOptions&lt;AppSettings&gt;&gt;</c>.
/// <list type="bullet">
/// <item><see cref="Default"/> / <see cref="Create"/>: a fresh instance with the defaults the manager
/// tests share (JWT secret, SQLite providers, default admin, <c>./temp</c>).</item>
/// <item><see cref="Options(Action{AppSettings}?)"/> / <see cref="Options(AppSettings)"/>: a fixed
/// <see cref="IOptions{TOptions}"/> for a SUT built once.</item>
/// <item><see cref="Mutable"/>: an <see cref="IOptions{TOptions}"/> whose value a test can swap after
/// the SUT dependencies were wired (what <c>mock.Setup(o =&gt; o.Value).Returns(...)</c> did).</item>
/// <item><see cref="As{T}"/> / <see cref="Options{T}(IOptions{AppSettings})"/>: a focused options class
/// bound from the same keys as an <see cref="AppSettings"/>, as the host binds it, so a test keeps
/// one settings object for every class it wires.</item>
/// </list>
/// </summary>
public static class TestSettings
{
    /// <summary>Test JWT secret (not used anywhere else).</summary>
    public const string Secret =
        "a2780070a24cfcaf5a4a43f931200ba0d19d8b86b3a7bd5123d9ad75b125f480fcce1f9b7f41a53abe2ba8456bd142d38c455302e0081e5139bc3fc9bf614497";

    /// <summary>A new <see cref="AppSettings"/> with the test defaults.</summary>
    public static AppSettings Default => Create();

    /// <summary>A new <see cref="AppSettings"/> with the test defaults, then <paramref name="tweak"/> applied.</summary>
    public static AppSettings Create(Action<AppSettings>? tweak = null)
    {
        var settings = new AppSettings
        {
            Secret = Secret,
            TokenExpirationInDays = 7,
            RevokedTokens = [""],
            AuthProvider = DbProvider.Sqlite,
            RegistryProvider = DbProvider.Sqlite,
            DefaultAdmin = new AdminInfo
            {
                Email = "admin@example.com",
                UserName = "admin",
                Password = "password"
            },
            TempPath = "./temp"
        };
        tweak?.Invoke(settings);
        return settings;
    }

    /// <summary>Fixed options holding <see cref="Create"/>(<paramref name="tweak"/>).</summary>
    public static IOptions<AppSettings> Options(Action<AppSettings>? tweak = null) => Options(Create(tweak));

    /// <summary>Fixed options holding exactly <paramref name="settings"/> (no test defaults added).</summary>
    public static IOptions<AppSettings> Options(AppSettings settings) =>
        Microsoft.Extensions.Options.Options.Create(settings);

    /// <summary>
    /// Options whose <see cref="MutableOptions{T}.Value"/> can be replaced later. It starts as
    /// <paramref name="initial"/> (null by default, as an unconfigured mock returned).
    /// </summary>
    public static MutableOptions<AppSettings> Mutable(AppSettings? initial = null) => new() { Value = initial! };

    /// <summary>
    /// The focused options class <typeparamref name="T"/> of <paramref name="settings"/>: the settings are
    /// written as the <c>AppSettings</c> section and <typeparamref name="T"/> is bound from its section with
    /// its defaults (<see cref="RegistryOptions.GetRegistryOptions{T}"/>). Null settings bind nothing.
    /// </summary>
    public static T As<T>(AppSettings? settings) where T : class, new()
    {
        var json = JsonConvert.SerializeObject(new { AppSettings = settings ?? new AppSettings() });
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
        return configuration.GetRegistryOptions<T>();
    }

    /// <summary>
    /// <typeparamref name="T"/> of the current value of <paramref name="settings"/>, read on each access
    /// (a <see cref="MutableOptions{T}"/> swapped later is seen by a SUT built later).
    /// </summary>
    public static IOptions<T> Options<T>(IOptions<AppSettings> settings) where T : class, new() =>
        new ProjectedOptions<T>(settings);

    /// <summary><typeparamref name="T"/> of <paramref name="settings"/> as fixed options.</summary>
    public static IOptions<T> Options<T>(AppSettings settings) where T : class, new() =>
        Microsoft.Extensions.Options.Options.Create(As<T>(settings));

    private sealed class ProjectedOptions<T>(IOptions<AppSettings> source) : IOptions<T> where T : class, new()
    {
        public T Value => As<T>(source.Value);
    }
}

/// <summary>An <see cref="IOptions{TOptions}"/> whose value tests can set at any time.</summary>
public sealed class MutableOptions<T> : IOptions<T> where T : class
{
    public T Value { get; set; } = null!;
}
