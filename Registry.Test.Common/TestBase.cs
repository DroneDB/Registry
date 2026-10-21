using Microsoft.Extensions.Logging;
using NUnit.Framework;
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
}