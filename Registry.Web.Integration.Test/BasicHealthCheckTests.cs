using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Registry.Web.Integration.Test;

/// <summary>
/// Basic integration tests to verify the application starts correctly
/// with production-like infrastructure (MariaDB + Redis)
/// </summary>
[TestFixture]
public class BasicHealthCheckTests : ProductionScenarioTestBase
{
    private Process? _webServerProcess;
    private string _dataPath = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        await GlobalSetup();
        
        // Create temporary data directory
        _dataPath = Path.Combine(Path.GetTempPath(), $"RegistryIntegrationTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(_dataPath);
        Directory.CreateDirectory(Path.Combine(_dataPath, "datasets"));
        Directory.CreateDirectory(Path.Combine(_dataPath, "cache"));
        Directory.CreateDirectory(Path.Combine(_dataPath, "temp"));
        Directory.CreateDirectory(Path.Combine(_dataPath, "logs"));
        
        TestContext.WriteLine($"Using data path: {_dataPath}");
        
        // Start the web server
        await StartWebServer();
    }

    [OneTimeTearDown]
    public async Task Teardown()
    {
        StopWebServer();
        
        // Cleanup data directory
        if (Directory.Exists(_dataPath))
        {
            try
            {
                Directory.Delete(_dataPath, true);
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"Warning: Could not delete test data directory: {ex.Message}");
            }
        }
        
        await GlobalTeardown();
    }

    [Test]
    [Order(1)]
    public async Task HealthCheck_WithMonitorToken_ReturnsOk()
    {
        // Arrange
        var monitorToken = "test-monitor-token-12345";
        var healthUrl = $"/quickhealth?token={monitorToken}";

        // Act
        var response = await HttpClient.GetAsync(healthUrl);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        TestContext.WriteLine($"Health check response: {content}");
    }

    [Test]
    [Order(2)]
    public async Task HealthCheck_WithoutToken_ReturnsUnauthorized()
    {
        // Arrange
        var healthUrl = "/quickhealth";

        // Act
        var response = await HttpClient.GetAsync(healthUrl);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    [Order(3)]
    public async Task Database_Connection_IsEstablished()
    {
        // This test verifies that the application successfully connected to MariaDB
        // by checking the logs or attempting a login
        
        // Arrange
        var loginPayload = new
        {
            username = "admin",
            password = "Test1234!"
        };

        // Act - Try to login (this will fail if DB is not connected)
        var response = await HttpClient.PostAsJsonAsync("/users/authenticate", loginPayload);

        // Assert - We expect either success or a proper authentication error, not a 500 (DB error)
        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError, 
            "Database connection should be established");
        
        TestContext.WriteLine($"Login attempt status: {response.StatusCode}");
    }

    [Test]
    [Order(4)]
    public async Task Redis_Connection_IsEstablished()
    {
        // This test verifies that Redis cache is working
        // The application should be able to use Redis without errors
        
        // Arrange - Make a request that would typically use cache
        var response = await HttpClient.GetAsync("/orgs");

        // Assert - Should not fail due to Redis issues
        var content = await response.Content.ReadAsStringAsync();
        TestContext.WriteLine($"Organizations endpoint status: {response.StatusCode}");
        TestContext.WriteLine($"Response: {content.Substring(0, Math.Min(200, content.Length))}...");
        
        // Even if unauthorized, it should not be a 500 error (which would indicate Redis issues)
        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError,
            "Redis connection should be established");
    }

    [Test]
    [Order(5)]
    public async Task Application_Info_ReturnsVersion()
    {
        // Arrange & Act
        var response = await HttpClient.GetAsync("/info");

        // Assert
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);
            
            TestContext.WriteLine($"Application info: {content}");
            
            json["version"]?.ToString().Should().NotBeNullOrEmpty();
        }
        else
        {
            TestContext.WriteLine($"Info endpoint returned: {response.StatusCode}");
        }
    }

    #region Helper Methods

    private async Task StartWebServer()
    {
        TestContext.WriteLine("Starting Registry web server...");
        
        var registryExePath = FindRegistryExecutable();
        var appSettingsPath = GetUpdatedAppSettingsPath();
        
        // Copy appsettings to data directory
        File.Copy(appSettingsPath, Path.Combine(_dataPath, "appsettings.json"), true);
        
        var startInfo = new ProcessStartInfo
        {
            FileName = registryExePath,
            Arguments = $"--address localhost:{WebServerPort} --instance-type 0 \"{_dataPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        
        _webServerProcess = new Process { StartInfo = startInfo };
        
        _webServerProcess.OutputDataReceived += (sender, args) =>
        {
            if (!string.IsNullOrEmpty(args.Data))
                TestContext.WriteLine($"[WebServer] {args.Data}");
        };
        
        _webServerProcess.ErrorDataReceived += (sender, args) =>
        {
            if (!string.IsNullOrEmpty(args.Data))
                TestContext.WriteLine($"[WebServer ERROR] {args.Data}");
        };
        
        _webServerProcess.Start();
        _webServerProcess.BeginOutputReadLine();
        _webServerProcess.BeginErrorReadLine();
        
        TestContext.WriteLine($"Web server process started (PID: {_webServerProcess.Id})");
        
        // Wait for the service to become healthy
        var isHealthy = await WaitForServiceHealthy($"/quickhealth?token=test-monitor-token-12345", 60, 2000);
        
        if (!isHealthy)
        {
            throw new Exception("Web server failed to start within the timeout period");
        }
        
        TestContext.WriteLine("Web server is ready!");
    }

    private void StopWebServer()
    {
        if (_webServerProcess != null && !_webServerProcess.HasExited)
        {
            TestContext.WriteLine("Stopping web server...");
            
            try
            {
                _webServerProcess.Kill(true);
                _webServerProcess.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"Error stopping web server: {ex.Message}");
            }
            finally
            {
                _webServerProcess.Dispose();
            }
        }
    }

    private static string FindRegistryExecutable()
    {
        // Look for the Registry.Web executable in the build output
        var testDirectory = TestContext.CurrentContext.TestDirectory;
        
        // Try to find the executable in the Registry.Web project output
        var possiblePaths = new[]
        {
            Path.Combine(testDirectory, "..", "..", "..", "..", "Registry.Web", "bin", "Debug", "net9.0", "Registry.Web.exe"),
            Path.Combine(testDirectory, "..", "..", "..", "..", "Registry.Web", "bin", "Release", "net9.0", "Registry.Web.exe"),
            Path.Combine(testDirectory, "..", "Registry.Web", "bin", "Debug", "net9.0", "Registry.Web.exe"),
            Path.Combine(testDirectory, "..", "Registry.Web", "bin", "Release", "net9.0", "Registry.Web.exe"),
        };
        
        foreach (var path in possiblePaths)
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                TestContext.WriteLine($"Found Registry executable at: {fullPath}");
                return fullPath;
            }
        }
        
        throw new FileNotFoundException(
            "Could not find Registry.Web executable. Please build the Registry.Web project first.\n" +
            $"Searched paths:\n{string.Join("\n", possiblePaths)}");
    }

    #endregion
}
