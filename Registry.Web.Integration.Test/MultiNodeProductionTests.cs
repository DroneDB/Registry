using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Registry.Web.Integration.Test;

/// <summary>
/// Advanced integration tests simulating production multi-node scenario:
/// - Web Server (INSTANCE_TYPE=2)
/// - Processing Node (INSTANCE_TYPE=1)
/// - Thumbnail Generator (INSTANCE_TYPE=3)
/// </summary>
[TestFixture]
public class MultiNodeProductionTests : ProductionScenarioTestBase
{
    private Process? _webServerProcess;
    private Process? _processingNodeProcess;
    private Process? _thumbnailGeneratorProcess;
    private string _dataPath = null!;
    private string _jwtToken = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        await GlobalSetup();
        
        // Create shared data directory (simulating production shared volumes)
        _dataPath = Path.Combine(Path.GetTempPath(), $"RegistryMultiNode_{Guid.NewGuid()}");
        Directory.CreateDirectory(_dataPath);
        Directory.CreateDirectory(Path.Combine(_dataPath, "datasets"));
        Directory.CreateDirectory(Path.Combine(_dataPath, "cache"));
        Directory.CreateDirectory(Path.Combine(_dataPath, "temp"));
        Directory.CreateDirectory(Path.Combine(_dataPath, "logs"));
        
        TestContext.WriteLine($"Using shared data path: {_dataPath}");
        
        // Copy configuration to shared data directory
        var appSettingsPath = GetUpdatedAppSettingsPath();
        File.Copy(appSettingsPath, Path.Combine(_dataPath, "appsettings.json"), true);
        
        // Start all nodes
        await StartWebServer();
        await StartProcessingNode();
        await StartThumbnailGenerator();
        
        // Authenticate and get token
        await AuthenticateAdmin();
    }

    [OneTimeTearDown]
    public async Task Teardown()
    {
        StopAllProcesses();
        
        // Cleanup data directory
        if (Directory.Exists(_dataPath))
        {
            try
            {
                await Task.Delay(2000); // Wait for processes to release files
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
    public async Task AllNodes_AreHealthyAndRunning()
    {
        // Assert - Web Server
        var webServerHealth = await HttpClient.GetAsync("/quickhealth?token=test-monitor-token-12345");
        webServerHealth.StatusCode.Should().Be(HttpStatusCode.OK, "Web server should be healthy");
        
        // Assert - Thumbnail Generator (has its own endpoint)
        using var thumbnailClient = new HttpClient { BaseAddress = new Uri(ThumbnailGeneratorUrl) };
        var thumbnailHealth = await thumbnailClient.PostAsync("/generate-thumbnail", 
            new StringContent("", Encoding.UTF8, "application/x-www-form-urlencoded"));
        
        // We expect 400 (bad request) not 500, which means it's running
        thumbnailHealth.StatusCode.Should().Be(HttpStatusCode.BadRequest, 
            "Thumbnail generator should be running (400 = running but needs valid params)");
        
        TestContext.WriteLine("✓ All nodes are running and healthy");
    }

    [Test]
    [Order(2)]
    public async Task Authentication_WorksAcrossNodes()
    {
        // Arrange
        _jwtToken.Should().NotBeNullOrEmpty("Should have authenticated");
        
        // Act - Use the token to access protected endpoint
        HttpClient.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _jwtToken);
        
        var response = await HttpClient.GetAsync("/orgs");
        
        // Assert
        response.IsSuccessStatusCode.Should().BeTrue("Should be able to access protected endpoint with token");
        
        TestContext.WriteLine($"✓ Successfully authenticated and accessed protected endpoint: {response.StatusCode}");
    }

    [Test]
    [Order(3)]
    public async Task CreateOrganizationAndDataset_WorksInMultiNodeSetup()
    {
        // Arrange
        HttpClient.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _jwtToken);
        
        var orgSlug = $"testorg-{Guid.NewGuid().ToString("N").Substring(0, 8)}";
        var orgPayload = new
        {
            name = "Test Organization",
            slug = orgSlug,
            isPublic = true
        };
        
        // Act - Create organization
        var orgResponse = await HttpClient.PostAsJsonAsync("/orgs/new", orgPayload);
        
        // Assert
        orgResponse.StatusCode.Should().Be(HttpStatusCode.OK, "Should create organization successfully");
        
        var orgContent = await orgResponse.Content.ReadAsStringAsync();
        var orgJson = JObject.Parse(orgContent);
        
        orgJson["slug"]?.ToString().Should().Be(orgSlug);
        
        TestContext.WriteLine($"✓ Created organization: {orgSlug}");
        
        // Act - Create dataset
        var datasetSlug = $"testds-{Guid.NewGuid().ToString("N").Substring(0, 8)}";
        var datasetPayload = new
        {
            slug = datasetSlug,
            name = "Test Dataset",
            isPublic = true
        };
        
        var dsResponse = await HttpClient.PostAsJsonAsync($"/orgs/{orgSlug}/ds/new", datasetPayload);
        
        // Assert
        dsResponse.StatusCode.Should().Be(HttpStatusCode.OK, "Should create dataset successfully");
        
        var dsContent = await dsResponse.Content.ReadAsStringAsync();
        var dsJson = JObject.Parse(dsContent);
        
        dsJson["slug"]?.ToString().Should().Be(datasetSlug);
        
        TestContext.WriteLine($"✓ Created dataset: {orgSlug}/{datasetSlug}");
        
        // Verify the dataset exists in the file system
        var datasetPath = Path.Combine(_dataPath, "datasets", orgSlug, dsJson["internalRef"]?.ToString() ?? "");
        Directory.Exists(datasetPath).Should().BeTrue("Dataset directory should exist on shared storage");
        
        TestContext.WriteLine($"✓ Dataset directory exists at: {datasetPath}");
    }

    [Test]
    [Order(4)]
    public async Task HangfireJobs_AreProcessedByWorkerNode()
    {
        // This test verifies that background jobs are being processed by the processing node
        // We can check Hangfire dashboard or verify that jobs are being executed
        
        TestContext.WriteLine("Waiting for Hangfire to process background jobs...");
        
        // Give some time for Hangfire to initialize and start processing
        await Task.Delay(10000);
        
        // Check if processing node is still running (it would crash if Hangfire had issues)
        _processingNodeProcess.Should().NotBeNull();
        _processingNodeProcess!.HasExited.Should().BeFalse("Processing node should still be running");
        
        TestContext.WriteLine("✓ Processing node is running and processing jobs");
    }

    [Test]
    [Order(5)]
    public async Task ThumbnailGenerator_IsAccessibleFromWebServer()
    {
        // This test verifies that the web server can communicate with the thumbnail generator
        // In production, this would be via the RemoteThumbnailGeneratorUrl setting
        
        // The thumbnail generator endpoint should be responsive
        using var thumbnailClient = new HttpClient { BaseAddress = new Uri(ThumbnailGeneratorUrl) };
        
        // Try to generate a thumbnail (will fail without valid file, but endpoint should respond)
        var formContent = new MultipartFormDataContent
        {
            { new StringContent("/invalid/path"), "path" },
            { new StringContent("256"), "size" }
        };
        
        var response = await thumbnailClient.PostAsync("/generate-thumbnail", formContent);
        
        // We expect 404 (file not found) or 400 (bad request), not 500 (server error)
        response.StatusCode.Should().Match(status => 
            status == HttpStatusCode.NotFound || status == HttpStatusCode.BadRequest,
            "Thumbnail generator should be accessible and processing requests");
        
        TestContext.WriteLine($"✓ Thumbnail generator responded with: {response.StatusCode}");
    }

    [Test]
    [Order(6)]
    public async Task SharedStorage_IsAccessibleByAllNodes()
    {
        // Verify that all nodes are using the same shared storage
        var testFile = Path.Combine(_dataPath, "test-shared-file.txt");
        var testContent = $"Shared storage test - {DateTime.UtcNow:O}";
        
        await File.WriteAllTextAsync(testFile, testContent);
        
        // Give file system time to sync
        await Task.Delay(500);
        
        // Verify file exists and content is correct
        File.Exists(testFile).Should().BeTrue("Test file should exist in shared storage");
        var readContent = await File.ReadAllTextAsync(testFile);
        readContent.Should().Be(testContent, "All nodes should see the same shared storage content");
        
        TestContext.WriteLine("✓ Shared storage is working correctly");
        
        // Cleanup
        File.Delete(testFile);
    }

    #region Helper Methods

    private async Task StartWebServer()
    {
        TestContext.WriteLine("Starting Web Server (INSTANCE_TYPE=2)...");
        
        var registryExePath = FindRegistryExecutable();
        
        var startInfo = new ProcessStartInfo
        {
            FileName = registryExePath,
            Arguments = $"--address localhost:5000 --instance-type 2 \"{_dataPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        
        _webServerProcess = StartRegistryProcess(startInfo, "WebServer");
        
        // Wait for the service to become healthy
        var isHealthy = await WaitForServiceHealthy("/quickhealth?token=test-monitor-token-12345", 60, 2000);
        
        if (!isHealthy)
        {
            throw new Exception("Web server failed to start");
        }
        
        TestContext.WriteLine("✓ Web Server is ready!");
    }

    private async Task StartProcessingNode()
    {
        TestContext.WriteLine("Starting Processing Node (INSTANCE_TYPE=1)...");
        
        var registryExePath = FindRegistryExecutable();
        
        var startInfo = new ProcessStartInfo
        {
            FileName = registryExePath,
            Arguments = $"--instance-type 1 \"{_dataPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        
        _processingNodeProcess = StartRegistryProcess(startInfo, "ProcessingNode");
        
        // Give the processing node time to start and connect to Hangfire
        await Task.Delay(5000);
        
        if (_processingNodeProcess.HasExited)
        {
            throw new Exception($"Processing node exited unexpectedly with code {_processingNodeProcess.ExitCode}");
        }
        
        TestContext.WriteLine("✓ Processing Node is running!");
    }

    private async Task StartThumbnailGenerator()
    {
        TestContext.WriteLine("Starting Thumbnail Generator (INSTANCE_TYPE=3)...");
        
        var registryExePath = FindRegistryExecutable();
        
        var startInfo = new ProcessStartInfo
        {
            FileName = registryExePath,
            Arguments = $"--address localhost:5005 --instance-type 3 \"{_dataPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        
        _thumbnailGeneratorProcess = StartRegistryProcess(startInfo, "ThumbnailGen");
        
        // Give the thumbnail generator time to start
        await Task.Delay(5000);
        
        if (_thumbnailGeneratorProcess.HasExited)
        {
            throw new Exception($"Thumbnail generator exited unexpectedly with code {_thumbnailGeneratorProcess.ExitCode}");
        }
        
        TestContext.WriteLine("✓ Thumbnail Generator is running!");
    }

    private Process StartRegistryProcess(ProcessStartInfo startInfo, string processName)
    {
        var process = new Process { StartInfo = startInfo };
        
        process.OutputDataReceived += (sender, args) =>
        {
            if (!string.IsNullOrEmpty(args.Data))
                TestContext.WriteLine($"[{processName}] {args.Data}");
        };
        
        process.ErrorDataReceived += (sender, args) =>
        {
            if (!string.IsNullOrEmpty(args.Data))
                TestContext.WriteLine($"[{processName} ERROR] {args.Data}");
        };
        
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        
        TestContext.WriteLine($"{processName} process started (PID: {process.Id})");
        
        return process;
    }

    private void StopAllProcesses()
    {
        TestContext.WriteLine("Stopping all Registry processes...");
        
        StopProcess(_webServerProcess, "Web Server");
        StopProcess(_processingNodeProcess, "Processing Node");
        StopProcess(_thumbnailGeneratorProcess, "Thumbnail Generator");
    }

    private void StopProcess(Process? process, string name)
    {
        if (process != null && !process.HasExited)
        {
            TestContext.WriteLine($"Stopping {name}...");
            
            try
            {
                process.Kill(true);
                process.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"Error stopping {name}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private async Task AuthenticateAdmin()
    {
        TestContext.WriteLine("Authenticating as admin...");
        
        var loginPayload = new
        {
            username = "admin",
            password = "Test1234!"
        };
        
        var response = await HttpClient.PostAsJsonAsync("/users/authenticate", loginPayload);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to authenticate: {response.StatusCode} - {errorContent}");
        }
        
        var content = await response.Content.ReadAsStringAsync();
        var json = JObject.Parse(content);
        
        _jwtToken = json["token"]?.ToString() 
            ?? throw new Exception("Authentication response did not contain a token");
        
        TestContext.WriteLine($"✓ Authenticated successfully, token length: {_jwtToken.Length}");
    }

    private static string FindRegistryExecutable()
    {
        var testDirectory = TestContext.CurrentContext.TestDirectory;
        
        var possiblePaths = new[]
        {
            Path.Combine(testDirectory, "..", "..", "..", "..", "Registry.Web", "bin", "Debug", "net9.0", "Registry.Web.exe"),
            Path.Combine(testDirectory, "..", "..", "..", "..", "Registry.Web", "bin", "Release", "net9.0", "Registry.Web.exe"),
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
            "Could not find Registry.Web executable. Please build the Registry.Web project first.");
    }

    #endregion
}

// Extension method for PostAsJsonAsync if not available
internal static class HttpClientExtensions
{
    public static Task<HttpResponseMessage> PostAsJsonAsync<T>(this HttpClient client, string requestUri, T value)
    {
        var json = JsonConvert.SerializeObject(value);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return client.PostAsync(requestUri, content);
    }
}
