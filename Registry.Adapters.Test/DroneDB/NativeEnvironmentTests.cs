using System;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Shouldly;

namespace Registry.Adapters.Test.DroneDB;

/// <summary>
/// NativeEnvironment writes the C runtime's environment (what libddb and GDAL read with getenv) and reads it
/// back from there, so these tests go through the native calls, not only .NET's copy.
/// </summary>
[TestFixture]
[NonParallelizable]
public class NativeEnvironmentTests
{
    private string _name = null!;

    [SetUp]
    public void SetUp() => _name = "REGISTRY_NATIVE_ENV_TEST_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [TearDown]
    public void TearDown() => NativeEnvironment.Set(_name, null);

    [Test]
    public void Set_IsReadBackFromTheNativeEnvironment_AndNullRemovesIt()
    {
        NativeEnvironment.Set(_name, "first");
        NativeEnvironment.Get(_name).ShouldBe("first");
        Environment.GetEnvironmentVariable(_name).ShouldBe("first");

        NativeEnvironment.Set(_name, "second");
        NativeEnvironment.Get(_name).ShouldBe("second");

        NativeEnvironment.Set(_name, null);
        NativeEnvironment.Get(_name).ShouldBeNull();
        Environment.GetEnvironmentVariable(_name).ShouldBeNull();
    }

    [Test]
    public void Set_KeepsANonAsciiValue_OnUnix()
    {
        // The C runtime of Windows reads the ANSI code page, which may not hold these characters.
        if (OperatingSystem.IsWindows())
            Assert.Ignore("Unix only: the native environment is UTF-8 there");

        NativeEnvironment.Set(_name, "/dati/città/ортофото");
        NativeEnvironment.Get(_name).ShouldBe("/dati/città/ортофото");
    }

    [Test]
    public void SetIfUnset_KeepsAnExistingValue()
    {
        NativeEnvironment.SetIfUnset(_name, "first").ShouldBeTrue();
        NativeEnvironment.SetIfUnset(_name, "second").ShouldBeFalse();
        NativeEnvironment.Get(_name).ShouldBe("first");
    }
}
