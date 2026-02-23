using System.Threading.Tasks;

namespace Registry.Ports;

/// <summary>
/// Provides pattern-based cache key scanning and deletion.
/// Implementation varies by cache provider (Redis, InMemory, etc.).
/// </summary>
public interface ICacheKeyScanner
{
    /// <summary>
    /// Removes all cache entries matching the given pattern.
    /// Pattern uses the internal key format (e.g., ":thumb:org/ds:*").
    /// Implementations handle provider-specific details like key prefixes.
    /// </summary>
    /// <param name="pattern">The pattern to match cache keys against</param>
    /// <returns>Number of entries removed</returns>
    Task<int> RemoveByPatternAsync(string pattern);
}
