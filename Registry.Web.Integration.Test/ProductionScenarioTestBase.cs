using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using NUnit.Framework;
using Testcontainers.MariaDb;
using Testcontainers.Redis;

namespace Registry.Web.Integration.Test;

/// <summary>
/// Base class for production scenario integration tests.
/// Manages the lifecycle of MariaDB and Redis containers using Testcontainers.
/// </summary>
public abstract class ProductionScenarioTestBase
{
    protected MariaDbContainer MariaDbContainer { get; private set; } = null!;
    protected RedisContainer RedisContainer { get; private set; } = null!;
    protected HttpClient HttpClient { get; private set; } = null!;
    
    protected string MariaDbConnectionString => MariaDbContainer.GetConnectionString();
    protected string RedisConnectionString => $"{RedisContainer.Hostname}:{RedisContainer.GetMappedPublicPort(6379)}";

    // Use dynamic ports to avoid conflicts
    protected int WebServerPort { get; private set; }
    protected int ThumbnailGeneratorPort { get; private set; }
    
    protected string WebServerUrl => $"http://localhost:{WebServerPort}";
    protected string ThumbnailGeneratorUrl => $"http://localhost:{ThumbnailGeneratorPort}";

    [OneTimeSetUp]
    public async Task GlobalSetup()
    {
        TestContext.WriteLine("=== Starting Production Scenario Integration Test Setup ===");
        
        // Assign dynamic ports for the application
        WebServerPort = GetAvailablePort(5000);
        ThumbnailGeneratorPort = GetAvailablePort(5005);
        TestContext.WriteLine($"Using dynamic ports - WebServer: {WebServerPort}, Thumbnail: {ThumbnailGeneratorPort}");
        
        // Start MariaDB container with improved wait strategy
        TestContext.WriteLine("Starting MariaDB container...");
        MariaDbContainer = new MariaDbBuilder()
            .WithImage("mariadb:latest")
            .WithDatabase("RegistryAuthTest")
            .WithUsername("root")
            .WithPassword("testpassword")
            .WithPortBinding(3306, true) // Random port mapping
            .WithResourceMapping(GetInitializationScript(), "/docker-entrypoint-initdb.d/initialize.sql")
            .WithCleanUp(true) // Ensure cleanup
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilPortIsAvailable(3306)
                .UntilMessageIsLogged("mariadbd: ready for connections")) // Wait for ready message
            .Build();
        
        await MariaDbContainer.StartAsync();
        TestContext.WriteLine($"MariaDB started on port {MariaDbContainer.GetMappedPublicPort(3306)}");
        TestContext.WriteLine($"Connection string: {MariaDbConnectionString}");

        // Start Redis container with improved wait strategy
        TestContext.WriteLine("Starting Redis container...");
        RedisContainer = new RedisBuilder()
            .WithImage("redis:8.2-alpine")
            .WithPortBinding(6379, true) // Random port mapping
            .WithCleanUp(true) // Ensure cleanup
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilPortIsAvailable(6379)
                .UntilMessageIsLogged("Ready to accept connections"))
            .Build();
        
        await RedisContainer.StartAsync();
        TestContext.WriteLine($"Redis started on port {RedisContainer.GetMappedPublicPort(6379)}");
        TestContext.WriteLine($"Redis connection: {RedisConnectionString}");

        // Initialize HttpClient
        HttpClient = new HttpClient
        {
            BaseAddress = new Uri(WebServerUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };

        TestContext.WriteLine("=== Setup Complete ===\n");
    }

    [OneTimeTearDown]
    public async Task GlobalTeardown()
    {
        TestContext.WriteLine("\n=== Starting Production Scenario Integration Test Teardown ===");
        
        HttpClient?.Dispose();
        
        if (MariaDbContainer != null)
        {
            TestContext.WriteLine("Stopping MariaDB container...");
            await MariaDbContainer.StopAsync();
            await MariaDbContainer.DisposeAsync();
        }
        
        if (RedisContainer != null)
        {
            TestContext.WriteLine("Stopping Redis container...");
            await RedisContainer.StopAsync();
            await RedisContainer.DisposeAsync();
        }
        
        TestContext.WriteLine("=== Teardown Complete ===");
    }

    /// <summary>
    /// Gets the path to the SQL initialization script
    /// </summary>
    private static string GetInitializationScript()
    {
        var testDirectory = TestContext.CurrentContext.TestDirectory;
        var scriptPath = Path.Combine(testDirectory, "initialize-test.sql");
        
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"Initialization script not found at {scriptPath}");
        }
        
        return scriptPath;
    }

    /// <summary>
    /// Updates the appsettings configuration file with the actual container connection strings
    /// </summary>
    protected string GetUpdatedAppSettingsPath()
    {
        var testDirectory = TestContext.CurrentContext.TestDirectory;
        var originalPath = Path.Combine(testDirectory, "appsettings-integration.json");
        var updatedPath = Path.Combine(testDirectory, "appsettings-integration-runtime.json");

        if (!File.Exists(originalPath))
        {
            throw new FileNotFoundException($"Configuration file not found at {originalPath}");
        }

        // Read and update the configuration
        var json = File.ReadAllText(originalPath);
        
        // Replace placeholder connection strings with actual container endpoints
        json = json.Replace("localhost:6379", RedisConnectionString);
        json = json.Replace("Server=localhost", $"Server={MariaDbContainer.Hostname};Port={MariaDbContainer.GetMappedPublicPort(3306)}");
        
        File.WriteAllText(updatedPath, json);
        
        return updatedPath;
    }

    /// <summary>
    /// Waits for a service to become healthy by polling its health endpoint
    /// </summary>
    protected async Task<bool> WaitForServiceHealthy(string healthUrl, int maxRetries = 30, int delayMs = 1000)
    {
        TestContext.WriteLine($"Waiting for service to become healthy: {healthUrl}");
        
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                var response = await HttpClient.GetAsync(healthUrl);
                if (response.IsSuccessStatusCode)
                {
                    TestContext.WriteLine($"Service is healthy after {i + 1} attempts");
                    return true;
                }
            }
            catch (HttpRequestException)
            {
                // Service not ready yet
            }
            
            await Task.Delay(delayMs);
        }
        
        TestContext.WriteLine($"Service failed to become healthy after {maxRetries} attempts");
        return false;
    }

    /// <summary>
    /// Gets an available port starting from the preferred port
    /// </summary>
    protected static int GetAvailablePort(int preferredPort)
    {
        // Try the preferred port first
        if (IsPortAvailable(preferredPort))
            return preferredPort;
        
        // If not available, find the next available port
        for (int port = preferredPort + 1; port < preferredPort + 100; port++)
        {
            if (IsPortAvailable(port))
                return port;
        }
        
        // If still not found, let OS assign a random port
        using var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        socket.Start();
        int assignedPort = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return assignedPort;
    }

    /// <summary>
    /// Checks if a port is available
    /// </summary>
    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            socket.Start();
            socket.Stop();
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }
}
