using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Storage;

namespace Registry.Web.Services.UpdateCheck;

/// <summary>The content of <c>.observed-base-url</c>.</summary>
/// <param name="BaseUrl">The public URL a request was served on.</param>
/// <param name="ObservedAt">When it was written (UTC).</param>
public sealed record ObservedBaseUrl(string BaseUrl, DateTime ObservedAt);

/// <summary>
/// The public URL the deployment was reached on, remembered in <c>.observed-base-url</c> of the storage folder for the
/// nodes that do not serve requests (the processing node that sends the update check). A web node writes it only when
/// the value changes, and at most once per <see cref="WriteInterval"/>.
/// </summary>
public sealed class ObservedBaseUrlStore
{
    /// <summary>The shortest time between two writes of one node.</summary>
    public static readonly TimeSpan WriteInterval = TimeSpan.FromHours(1);

    private readonly StorageDocument<ObservedBaseUrl> _file;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private bool _loaded;
    private string? _lastValue;
    private DateTime _lastWriteAt = DateTime.MinValue;

    public ObservedBaseUrlStore(IOptions<StorageOptions> storage, IFileSystem fs, TimeProvider time, ILogger<ObservedBaseUrlStore> logger)
    {
        _file = new StorageDocument<ObservedBaseUrl>(storage, fs, MagicStrings.ObservedBaseUrlFileName, logger);
        _time = time;
    }

    /// <summary>The URL in the file; null when there is none.</summary>
    public string? Read() => _file.Read()?.BaseUrl;

    /// <summary>
    /// Records <paramref name="baseUrl"/> if it is public (<see cref="PublicHost.IsPublic(string?)"/>), differs from what the file
    /// holds and this node did not write within <see cref="WriteInterval"/>.
    /// </summary>
    /// <returns>Whether the file was written.</returns>
    public bool Observe(string baseUrl)
    {
        // Every request lands here: the usual one (the value already recorded) costs a comparison
        if (string.Equals(Volatile.Read(ref _lastValue), baseUrl, StringComparison.Ordinal) || !PublicHost.IsPublic(baseUrl))
            return false;

        lock (_gate)
        {
            if (!_loaded)
            {
                _lastValue = Read();
                _loaded = true;
            }

            var now = _time.GetUtcNow().UtcDateTime;
            if (string.Equals(_lastValue, baseUrl, StringComparison.Ordinal) || now - _lastWriteAt < WriteInterval)
                return false;

            _file.Write(new ObservedBaseUrl(baseUrl, now));
            Volatile.Write(ref _lastValue, baseUrl);
            _lastWriteAt = now;
            return true;
        }
    }
}
