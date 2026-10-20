#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Registry.Web.Utilities;

/// <summary>
/// Resolves the MySQL server version that EF Core needs for <c>UseMySql</c> once per connection string,
/// lazily on first use, instead of probing the server (<see cref="ServerVersion.AutoDetect(string)"/>
/// opens a connection) every time a scope builds its <see cref="DbContextOptions"/>.
/// <para>
/// The probe is deliberately not run at registration: a database that is not reachable yet must not
/// crash start-up, and EF's retry-on-failure does not cover the probe. A failed probe is not cached,
/// so the next scope tries again. A pinned version (<see cref="PinnedVersionKey"/>) skips the probe.
/// </para>
/// </summary>
public sealed class MySqlServerVersionCache
{
    /// <summary>Configuration key of the optional pinned server version, e.g. <c>8.0.36-mysql</c> or <c>10.11.6-mariadb</c>.</summary>
    public const string PinnedVersionKey = "AppSettings:MySqlServerVersion";

    private readonly ServerVersion? _pinned;
    private readonly Func<string, ServerVersion> _probe;
    private readonly ConcurrentDictionary<string, Lazy<ServerVersion>> _detected = new(StringComparer.Ordinal);

    /// <param name="pinnedVersion">Server version to use without probing; null or blank to auto-detect.</param>
    /// <param name="probe">Version probe; defaults to <see cref="ServerVersion.AutoDetect(string)"/>.</param>
    /// <exception cref="ArgumentException">The pinned version cannot be parsed.</exception>
    public MySqlServerVersionCache(string? pinnedVersion = null, Func<string, ServerVersion>? probe = null)
    {
        _pinned = string.IsNullOrWhiteSpace(pinnedVersion) ? null : ParsePinned(pinnedVersion);
        _probe = probe ?? ServerVersion.AutoDetect;
    }

    /// <summary>Creates a cache honouring the pinned version in <paramref name="configuration"/>, if any.</summary>
    public static MySqlServerVersionCache FromConfiguration(IConfiguration configuration) =>
        new(configuration?[PinnedVersionKey]);

    /// <summary>Returns the server version for <paramref name="connectionString"/>, probing at most once on success.</summary>
    public ServerVersion Resolve(string connectionString)
    {
        if (_pinned != null)
            return _pinned;

        ArgumentNullException.ThrowIfNull(connectionString);

        var entry = _detected.GetOrAdd(connectionString,
            cs => new Lazy<ServerVersion>(() => _probe(cs)));
        try
        {
            return entry.Value;
        }
        catch
        {
            // Lazy caches the exception: drop this entry (only if still ours) so the next call retries
            _detected.TryRemove(new KeyValuePair<string, Lazy<ServerVersion>>(connectionString, entry));
            throw;
        }
    }

    private static ServerVersion ParsePinned(string pinnedVersion)
    {
        try
        {
            return ServerVersion.Parse(pinnedVersion.Trim());
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            throw new ArgumentException(
                $"Invalid {PinnedVersionKey} '{pinnedVersion}' (expected e.g. '8.0.36-mysql')", nameof(pinnedVersion), ex);
        }
    }
}
