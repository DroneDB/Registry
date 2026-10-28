using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// No-op cache key scanner for in-memory cache provider.
/// In-memory cache is cleared on application restart, so pattern-based
/// removal is not critical. Entries will expire naturally via their TTL.
/// </summary>
public class NullCacheKeyScanner : ICacheKeyScanner
{
    private readonly ILogger<NullCacheKeyScanner> _logger;

    public NullCacheKeyScanner(ILogger<NullCacheKeyScanner> logger)
    {
        _logger = logger;
    }

    public Task<int> RemoveByPatternAsync(string pattern)
    {
        _logger.LogDebug(
            "Pattern-based cache removal not available with in-memory provider. " +
            "Entries matching '{Pattern}' will expire naturally",
            pattern);
        return Task.FromResult(0);
    }
}
