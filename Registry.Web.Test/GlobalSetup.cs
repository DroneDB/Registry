using System;
using System.IO;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Common;

namespace Registry.Web.Test
{
    [SetUpFixture]
    public class GlobalSetup
    {
        [OneTimeSetUp]
        public void RunBeforeAnyTests()
        {
            Directory.SetCurrentDirectory(TestContext.CurrentContext.TestDirectory);

            var ddbFolder = CommonUtils.FindDdbFolder();
            if (ddbFolder == null)
                throw new Exception("DDB not found");

            CommonUtils.SetDefaultDllPath(ddbFolder);

            DDBWrapper.RegisterProcess(true);
        }
    }
}