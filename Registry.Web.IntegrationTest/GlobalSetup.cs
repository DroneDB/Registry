#nullable disable
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Web.Test.Support;
using Registry.Test.Common;

// Category taxonomy: every test of this assembly is an Integration test
[assembly: Category(TestCategories.Integration)]

namespace Registry.Web.Test;

/// <summary>
/// Loads native libddb once for the integration run (Registry.Web.UnitTest never does) and stops
/// the shared MariaDB container at the end.
/// </summary>
[SetUpFixture]
public class GlobalSetup
{
    [OneTimeSetUp]
    public void RunBeforeAnyTests()
    {
        Directory.SetCurrentDirectory(TestContext.CurrentContext.TestDirectory);

        DdbLibrary.UseFromPath();

        // What a host does at startup: the ABI handshake, then the initialization (DdbRuntimeService)
        var capabilities = DdbAbi.Verify();
        new NativeDdbRuntime().RegisterProcess(true);

        Debug.WriteLine($"Initialized ddb: {capabilities}");
    }

    [OneTimeTearDown]
    public Task RunAfterAllTests() => MariaDbServer.StopAsync();
}