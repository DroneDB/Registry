using System.IO;
using NUnit.Framework;

namespace Registry.Adapters.Test;

[SetUpFixture]
public class GlobalSetup
{
    [OneTimeSetUp]
    public void RunBeforeAnyTests()
    {
        Directory.SetCurrentDirectory(TestContext.CurrentContext.TestDirectory);
    }
}