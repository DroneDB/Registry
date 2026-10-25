#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Hosting;

// The rules of the options. They are the checks Program.VerifySettings ran on the storage
// folder's file before the host started, unchanged, so a configuration that started before starts now;
// they now see the effective configuration (file, environment, command line) and run at host start
// (ValidateOnStart) and in --check. Messages name the configuration key.

/// <summary>Collects the failures of one options class.</summary>
internal sealed class OptionsFailures
{
    private readonly List<string> _failures = [];

    public OptionsFailures Check(bool valid, string failure)
    {
        if (!valid)
            _failures.Add(failure);
        return this;
    }

    public ValidateOptionsResult Result() =>
        _failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(_failures);

    public static string Key(string name) => RegistryOptions.SectionName + ":" + name;

    public static bool AtLeastOneMinute(TimeSpan? value) => value is not { TotalMinutes: < 1 };
}

internal sealed class AuthTokenOptionsValidator : IValidateOptions<AuthTokenOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthTokenOptions options) => new OptionsFailures()
        .Check(!string.IsNullOrWhiteSpace(options.Secret),
            $"{OptionsFailures.Key(nameof(AuthTokenOptions.Secret))} is empty (the first start of a storage folder generates it)")
        .Check(options.TokenExpirationInDays >= 1,
            $"{OptionsFailures.Key(nameof(AuthTokenOptions.TokenExpirationInDays))} is not valid (must be greater than 0)")
        .Check(!string.IsNullOrWhiteSpace(options.AuthCookieName),
            $"{OptionsFailures.Key(nameof(AuthTokenOptions.AuthCookieName))} is empty")
        .Result();
}

internal sealed class IdentityProviderOptionsValidator : IValidateOptions<IdentityProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, IdentityProviderOptions options)
    {
        var admin = options.DefaultAdmin;
        var ldap = options.LdapSettings;
        var ldapKey = OptionsFailures.Key(nameof(IdentityProviderOptions.LdapSettings));

        return new OptionsFailures()
            .Check(admin is { Email: not null, Password: not null, UserName: not null },
                $"{OptionsFailures.Key(nameof(IdentityProviderOptions.DefaultAdmin))} is not valid (Email, UserName and Password are required)")
            .Check(!options.LdapEnabled || !options.ExternalAuthEnabled,
                $"Cannot enable both LDAP ({ldapKey}:Enabled) and external authentication ({OptionsFailures.Key(nameof(IdentityProviderOptions.ExternalAuthUrl))}) simultaneously")
            .Check(!options.LdapEnabled || !string.IsNullOrWhiteSpace(ldap!.Server),
                $"{ldapKey}:Server is required when LDAP is enabled")
            .Check(!options.LdapEnabled || !string.IsNullOrWhiteSpace(ldap!.BaseDn),
                $"{ldapKey}:BaseDn is required when LDAP is enabled")
            .Check(!options.LdapEnabled || (!string.IsNullOrWhiteSpace(ldap!.SearchFilter) &&
                                            ldap.SearchFilter.Contains("{0}", StringComparison.Ordinal)),
                $"{ldapKey}:SearchFilter must contain the '{{0}}' placeholder for the username")
            .Result();
    }
}

internal sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageOptions options) => new OptionsFailures()
        .Check(!string.IsNullOrWhiteSpace(options.DatasetsPath),
            $"{OptionsFailures.Key(nameof(StorageOptions.DatasetsPath))} is empty")
        .Check(!string.IsNullOrWhiteSpace(options.CachePath),
            $"{OptionsFailures.Key(nameof(StorageOptions.CachePath))} is empty")
        .Result();
}

internal sealed class CachingOptionsValidator : IValidateOptions<CachingOptions>
{
    public ValidateOptionsResult Validate(string? name, CachingOptions options) => new OptionsFailures()
        .Check(OptionsFailures.AtLeastOneMinute(options.ThumbnailsCacheExpiration),
            $"{OptionsFailures.Key(nameof(CachingOptions.ThumbnailsCacheExpiration))} is not valid (must be at least 1 minute)")
        .Check(OptionsFailures.AtLeastOneMinute(options.TilesCacheExpiration),
            $"{OptionsFailures.Key(nameof(CachingOptions.TilesCacheExpiration))} is not valid (must be at least 1 minute)")
        .Check(OptionsFailures.AtLeastOneMinute(options.DatasetVisibilityCacheExpiration),
            $"{OptionsFailures.Key(nameof(CachingOptions.DatasetVisibilityCacheExpiration))} is not valid (must be at least 1 minute)")
        .Check(OptionsFailures.AtLeastOneMinute(options.ClearCacheInterval),
            $"{OptionsFailures.Key(nameof(CachingOptions.ClearCacheInterval))} is not valid (must be at least 1 minute)")
        .Result();
}

internal sealed class HttpHostOptionsValidator : IValidateOptions<HttpHostOptions>
{
    public ValidateOptionsResult Validate(string? name, HttpHostOptions options)
    {
        var failures = new OptionsFailures()
            .Check(string.IsNullOrWhiteSpace(options.ExternalUrlOverride) ||
                   Uri.TryCreate(options.ExternalUrlOverride, UriKind.Absolute, out _),
                $"{OptionsFailures.Key(nameof(HttpHostOptions.ExternalUrlOverride))} is not a valid URL");

        // An invalid entry stops the start-up: ignoring it would silently stop trusting the proxy and
        // key every client on the proxy address
        foreach (var entry in TrustedProxies.InvalidProxies(options))
            failures.Check(false,
                $"{OptionsFailures.Key(nameof(HttpHostOptions.KnownProxies))} contains an invalid IP address: '{entry}'");
        foreach (var entry in TrustedProxies.InvalidNetworks(options))
            failures.Check(false,
                $"{OptionsFailures.Key(nameof(HttpHostOptions.KnownNetworks))} contains an invalid CIDR network: '{entry}'");

        return failures.Result();
    }
}

internal sealed class ShareOptionsValidator : IValidateOptions<ShareOptions>
{
    public ValidateOptionsResult Validate(string? name, ShareOptions options) => new OptionsFailures()
        .Check(options.BatchTokenLength >= 8,
            $"{OptionsFailures.Key(nameof(ShareOptions.BatchTokenLength))} is not valid (must be at least 8)")
        .Check(options.UploadBatchTimeout.TotalMinutes >= 1,
            $"{OptionsFailures.Key(nameof(ShareOptions.UploadBatchTimeout))} is not valid (must be at least 1 minute)")
        .Check(options.RandomDatasetNameLength >= 8,
            $"{OptionsFailures.Key(nameof(ShareOptions.RandomDatasetNameLength))} is not valid (must be at least 8)")
        .Result();
}

/// <summary>The keys of <c>AppSettings:HostBudget</c> (a new section, so new rules break nothing).</summary>
internal sealed class HostResourceOptionsValidator : IValidateOptions<HostResourceOptions>
{
    public ValidateOptionsResult Validate(string? name, HostResourceOptions options)
    {
        var budget = options.HostBudget;
        var key = OptionsFailures.Key(nameof(HostResourceOptions.HostBudget));

        return new OptionsFailures()
            .Check(budget.Cores >= 0, $"{key}:Cores must not be negative")
            .Check(budget.MemoryMb >= 0, $"{key}:MemoryMb must not be negative")
            .Check(budget.WebServers >= 0 && budget.ProcessingNodes >= 0 && budget.ThumbnailGenerators >= 0,
                $"{key}: the process counts must not be negative")
            .Check(budget.WebServerWeight > 0 && budget.ProcessingNodeWeight > 0 && budget.ThumbnailGeneratorWeight > 0,
                $"{key}: the weights must be greater than 0")
            .Check(budget.IoThreads >= 0, $"{key}:IoThreads must not be negative")
            .Result();
    }
}

/// <summary>The thumbnail generator fails closed: without the shared token its endpoint would read files for anyone.</summary>
internal sealed class ThumbnailGeneratorTokenValidator : IValidateOptions<ThumbnailOptions>
{
    public ValidateOptionsResult Validate(string? name, ThumbnailOptions options) => new OptionsFailures()
        .Check(!string.IsNullOrWhiteSpace(options.ThumbnailGeneratorToken),
            $"{OptionsFailures.Key(nameof(ThumbnailOptions.ThumbnailGeneratorToken))} is not set: set it to the value " +
            "the web server uses (both read the same appsettings.json)")
        .Result();
}

/// <summary>A processing node shares the job storage with the web server: an in-memory storage is the node's own.</summary>
internal sealed class ProcessingNodeDataStoreValidator : IValidateOptions<DataStoreOptions>
{
    public ValidateOptionsResult Validate(string? name, DataStoreOptions options) => new OptionsFailures()
        .Check(options.HangfireProvider != HangfireProvider.InMemory,
            $"{OptionsFailures.Key(nameof(DataStoreOptions.HangfireProvider))} InMemory is not supported for a processing node")
        .Result();
}

/// <summary>The reverse proxies whose forwarded headers are trusted (<see cref="HttpHostOptions"/>).</summary>
public static class TrustedProxies
{
    /// <summary>The configured proxies (default applied) as addresses; invalid entries are left out (the validator reports them).</summary>
    public static IReadOnlyList<IPAddress> Proxies(HttpHostOptions options) =>
        Entries(options.KnownProxies ?? HttpHostOptions.DefaultKnownProxies)
            .Select(entry => IPAddress.TryParse(entry, out var address) ? address : null)
            .OfType<IPAddress>().ToList();

    /// <summary>The configured networks; invalid entries are left out (the validator reports them).</summary>
    public static IReadOnlyList<System.Net.IPNetwork> Networks(HttpHostOptions options) =>
        Entries(options.KnownNetworks ?? [])
            .Select(entry => System.Net.IPNetwork.TryParse(entry, out var network) ? (System.Net.IPNetwork?)network : null)
            .OfType<System.Net.IPNetwork>().ToList();

    internal static IEnumerable<string?> InvalidProxies(HttpHostOptions options) =>
        (options.KnownProxies ?? []).Where(entry => entry == null || !IPAddress.TryParse(entry.Trim(), out _));

    internal static IEnumerable<string?> InvalidNetworks(HttpHostOptions options) =>
        (options.KnownNetworks ?? []).Where(entry => entry == null || !System.Net.IPNetwork.TryParse(entry.Trim(), out _));

    private static IEnumerable<string> Entries(IEnumerable<string?> entries) =>
        entries.Where(entry => entry != null).Select(entry => entry!.Trim());
}
