#nullable enable
using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Storage;

namespace Registry.Web.Services.UpdateCheck;

/// <summary><see cref="IUpdateCheckCache"/> in <c>.update-check-cache.json</c> of the storage folder.</summary>
public sealed class UpdateCheckCacheStore : IUpdateCheckCache
{
    private readonly StorageDocument<UpdateCheckCache> _file;
    private readonly Lock _gate = new();

    public UpdateCheckCacheStore(IOptions<StorageOptions> storage, IFileSystem fs, ILogger<UpdateCheckCacheStore> logger) =>
        _file = new StorageDocument<UpdateCheckCache>(storage, fs, MagicStrings.UpdateCheckCacheFileName, logger);

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
