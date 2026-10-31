namespace Registry.Web.Models.Configuration;

/// <summary>The folders the Registry stores data in (keys of the <c>AppSettings</c> section).</summary>
public sealed class StorageOptions : IHasConfigurationDefaults
{
    /// <summary>The temp folder used when <see cref="TempPath"/> is empty (relative to the storage folder).</summary>
    public const string DefaultTempPath = "./temp";

    /// <summary>Main storage path (branding, Hub).</summary>
    public string? StoragePath { get; set; }

    /// <summary>Storage path for datasets (required).</summary>
    public string? DatasetsPath { get; set; }

    /// <summary>File cache path (required).</summary>
    public string? CachePath { get; set; }

    /// <summary>Storage path for temp files; <see cref="DefaultTempPath"/> when empty.</summary>
    public string? TempPath { get; set; }

    /// <summary>
    /// Path to a shared directory for ASP.NET Core Data Protection key storage. Required when the
    /// processing node runs in a separate process and Redis caching is not used, so the worker can
    /// decrypt credentials the web host encrypted. When Redis is the cache provider the key ring is
    /// shared via Redis instead and this can be left null.
    /// </summary>
    public string? DataProtectionKeysPath { get; set; }

    /// <summary><paramref name="configured"/>, or <see cref="DefaultTempPath"/> when it is empty.</summary>
    public static string ResolveTempPath(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultTempPath : configured;

    public void ApplyDefaults() => TempPath = ResolveTempPath(TempPath);
}
