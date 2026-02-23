using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Utilities;
using Serilog;
using Serilog.Extensions.Logging;

namespace Registry.Test.Common;

public class TestBase
{
    /// <summary>
    /// Gets or sets the test context which provides
    /// information about and functionality for the current test run.
    /// </summary>

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Creates an ILoggerFactory configured with Serilog that writes to NUnit test output.
    /// This allows test logs to be captured and displayed in the test results.
    /// </summary>
    /// <param name="minimumLevel">The minimum log level to capture (defaults to Debug)</param>
    /// <returns>A configured ILoggerFactory instance</returns>
    protected ILoggerFactory CreateTestLoggerFactory(Serilog.Events.LogEventLevel minimumLevel = Serilog.Events.LogEventLevel.Debug)
    {
        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                standardErrorFromLevel: null)
            .CreateLogger();

        return new SerilogLoggerFactory(serilogLogger, dispose: true);
    }

    /// <summary>
    /// Creates an ILogger&lt;T&gt; configured with Serilog that writes to NUnit test output.
    /// This is a convenience method for creating strongly-typed loggers for testing.
    /// </summary>
    /// <typeparam name="T">The category type for the logger</typeparam>
    /// <param name="minimumLevel">The minimum log level to capture (defaults to Debug)</param>
    /// <returns>A configured ILogger&lt;T&gt; instance</returns>
    protected ILogger<T> CreateTestLogger<T>(Serilog.Events.LogEventLevel minimumLevel = Serilog.Events.LogEventLevel.Debug)
    {
        var factory = CreateTestLoggerFactory(minimumLevel);
        return factory.CreateLogger<T>();
    }

    /// <summary>
    /// Creates an ICacheManager instance configured with MemoryDistributedCache for testing.
    /// This provides a real cache implementation instead of mocking.
    /// </summary>
    /// <param name="minimumLevel">The minimum log level to capture (defaults to Debug)</param>
    /// <returns>A configured ICacheManager instance</returns>
    protected ICacheManager CreateTestCacheManager(Serilog.Events.LogEventLevel minimumLevel = Serilog.Events.LogEventLevel.Debug)
    {
        // var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var distributedCache = new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        var logger = CreateTestLogger<CacheManager>(minimumLevel);
        var nullScanner = new NullCacheKeyScanner(CreateTestLogger<NullCacheKeyScanner>(minimumLevel));
        return new CacheManager(distributedCache, logger, nullScanner);
    }

    /// <summary>
    /// Registers a fake dataset visibility cache provider for testing.
    /// This allows tests to use cache operations without requiring a real IDdbManager.
    /// The provider mimics the real implementation by calling IDdbManager to get the actual visibility.
    /// </summary>
    /// <param name="cacheManager">The cache manager to register the provider with</param>
    protected static void RegisterDatasetVisibilityCacheProvider(ICacheManager cacheManager)
    {
        // Use the shared factory to ensure consistency with production code
        cacheManager.Register(
            MagicStrings.DatasetVisibilityCacheSeed,
            CacheProviderFactories.CreateDatasetVisibilityProvider(),
            TimeSpan.FromMinutes(30));
    }
}