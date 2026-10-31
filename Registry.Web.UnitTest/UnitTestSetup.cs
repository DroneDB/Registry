using System.IO;
using NUnit.Framework;
using Registry.Web.Test.Support;
using Shouldly;
using Registry.Test.Common;

// Category taxonomy: every test of this assembly is a Unit test
[assembly: Category(TestCategories.Unit)]

namespace Registry.Web.Test;

/// <summary>
/// Runs once around the whole unit test run: libddb is blocked before the first test
/// (<see cref="NativeDdbGuard"/>), and the run fails if any test tried to load it or if a libddb
/// module is mapped in the process at the end.
/// </summary>
[SetUpFixture]
public sealed class UnitTestSetup
{
    [OneTimeSetUp]
    public void RunBeforeAnyTests()
    {
        Directory.SetCurrentDirectory(TestContext.CurrentContext.TestDirectory);
        NativeDdbGuard.Install();
    }

    [OneTimeTearDown]
    public void RunAfterAllTests()
    {
        NativeDdbGuard.Attempts.ShouldBeEmpty("tests that tried to load native libddb");
        NativeDdbGuard.LoadedModules().ShouldBeEmpty("libddb modules loaded in the unit test process");
    }
}
