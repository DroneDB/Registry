using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Web.Test.Support;
using Registry.Test.Common;

// Category taxonomy (RQ-04): every test of this assembly is an Integration test
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

        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            var ddbFolder = CommonUtils.FindDdbFolder();
            if (ddbFolder == null)
                throw new Exception("DDB not found");

            CommonUtils.SetDefaultDllPath(ddbFolder);
        }

        var nativeDdbWrapper = new NativeDdbWrapper(true);

        Debug.WriteLine($"Initialized ddb version: {nativeDdbWrapper.GetVersion()}");
    }

    [OneTimeTearDown]
    public Task RunAfterAllTests() => MariaDbServer.StopAsync();
}