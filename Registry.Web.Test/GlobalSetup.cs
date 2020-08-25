using System;
using System.IO;
using NUnit.Framework;

namespace Registry.Web.Test
{
    [SetUpFixture]
    public class GlobalSetup
    {
        [OneTimeSetUp]
        public void RunBeforeAnyTests()
        {
            Directory.SetCurrentDirectory(TestContext.CurrentContext.TestDirectory);
        }
    }
}
