using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Web.Test.Support;

namespace Registry.Web.Test;

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