#nullable enable
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Registry.Web.Models.Configuration;

/// <summary>The database providers (keys of the <c>AppSettings</c> section; the connection strings are in <c>ConnectionStrings</c>).</summary>
public sealed class DataStoreOptions
{
    /// <summary>Provider of the identity database.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public DbProvider AuthProvider { get; set; }

    /// <summary>Provider of the registry database.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public DbProvider RegistryProvider { get; set; }

    /// <summary>Provider of the Hangfire job storage.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public HangfireProvider HangfireProvider { get; set; }

    /// <summary>
    /// MySQL server version used by the MySql providers, e.g. "8.0.36-mysql" or "10.11.6-mariadb".
    /// When set, the server is never probed for its version; when empty it is auto-detected once
    /// per connection string on first use (see <c>MySqlServerVersionCache</c>).
    /// </summary>
    public string? MySqlServerVersion { get; set; }
}
