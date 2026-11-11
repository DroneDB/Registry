using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Shouldly;

namespace Registry.Adapters.Test.DroneDB;

/// <summary>
/// The ABI rule of the startup handshake. DroneDB has one version and the ABI follows it, so
/// Registry runs on libddb 2.x from 2.4 (ABI major 2, minor at least 4: every export it calls) and refuses a 1.x,
/// 2.0 or any 2.x before 2.4 (the 2.3 builds included) with a message that says which library it needs.
/// </summary>
[TestFixture]
public class DdbAbiTests
{
    private static uint Abi(int major, int minor) => (uint)((major << 16) | minor);

    [TestCase(2, 4)]
    [TestCase(2, 5)]
    [TestCase(2, 17)]
    public void RequireAbi_AcceptsLibddb2x_From24(int major, int minor)
    {
        DdbAbi.RequireAbi("2.4.0", Abi(major, minor)).ShouldBe((major, minor));
    }

    [TestCase("2.0.1", 0)]
    [TestCase("2.1.0", 1)]
    [TestCase("2.2.0", 2)]
    [TestCase("2.3.0", 3)]
    public void RequireAbi_RejectsAnOlderMinor_NamingTheMinorItNeeds(string version, int minor)
    {
        var ex = Should.Throw<DdbAbiMismatchException>(() => DdbAbi.RequireAbi(version, Abi(2, minor)));

        ex.Message.ShouldContain($"libddb {version} has ABI 2.{minor}");
        ex.Message.ShouldContain("(ABI 2.x with x >= 4)");
    }

    [TestCase(1, 0)]
    [TestCase(1, 1)]
    [TestCase(1, 5)]
    [TestCase(3, 0)]
    [TestCase(0, 9)]
    public void RequireAbi_RejectsAnotherMajor_NamingLibddb2x(int major, int minor)
    {
        var ex = Should.Throw<DdbAbiMismatchException>(() => DdbAbi.RequireAbi("1.12.2", Abi(major, minor)));

        ex.Message.ShouldContain($"libddb 1.12.2 has ABI {major}.{minor}");
        ex.Message.ShouldContain("libddb 2.x");
        ex.Message.ShouldContain("Registry needs libddb 2.x");
    }

    [Test]
    public void Decode_SplitsMajorAndMinor()
    {
        DdbAbi.Decode(Abi(2, 0)).ShouldBe((2, 0));
        DdbAbi.Decode(Abi(2, 4)).ShouldBe((2, 4));
        DdbAbi.Major.ShouldBe(2);
        DdbAbi.Minor.ShouldBe(4);
    }
}
