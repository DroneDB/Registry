#nullable enable
using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.UpdateCheck;

/// <summary><see cref="IUpdateCheckCache"/> in <c>.update-check-cache.json</c> of the storage folder.</summary>
public sealed class UpdateCheckCacheStore : IUpdateCheckCache
{
    private readonly StorageDocument<UpdateCheckCache> _file;
    private readonly Lock _gate = new();

    public UpdateCheckCacheStore(IOptions<StorageOptions> storage, ILogger<UpdateCheckCacheStore> logger) =>
        _file = new StorageDocument<UpdateCheckCache>(storage, MagicStrings.UpdateCheckCacheFileName, logger);

    /// <inheritdoc />
    public UpdateCheckCache Read() => _file.Read() ?? UpdateCheckCache.Empty;

    /// <inheritdoc />
    public void Update(Func<UpdateCheckCache, UpdateCheckCache> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (_gate)
            _file.Write(change(Read()));
    }
}
