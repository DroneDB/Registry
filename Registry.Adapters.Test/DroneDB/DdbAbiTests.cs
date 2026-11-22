using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Shouldly;

namespace Registry.Adapters.Test.DroneDB;

/// <summary>
/// The ABI rule of the startup handshake. DroneDB has one version and the ABI follows it, so
/// Registry runs on libddb 2.x from 2.10 (ABI major 2, minor at least 10: every export it calls since 2.4, the
/// DDBConfigure setting artefactCacheMb of 2.6, DDBRescanEx2 of 2.9 and DDBFindCoverage of 2.10) and refuses a 1.x, 2.0 or any 2.x before 2.10 (2.4 to 2.9 included)
/// with a message that says which library it needs.
/// </summary>
[TestFixture]
public class DdbAbiTests
{
    private static uint Abi(int major, int minor) => (uint)((major << 16) | minor);

    [TestCase(2, 10)]
    [TestCase(2, 11)]
    [TestCase(2, 17)]
    public void RequireAbi_AcceptsLibddb2x_From210(int major, int minor)
    {
        DdbAbi.RequireAbi("2.10.0", Abi(major, minor)).ShouldBe((major, minor));
    }

    [TestCase("2.0.1", 0)]
    [TestCase("2.1.0", 1)]
    [TestCase("2.2.0", 2)]
    [TestCase("2.3.0", 3)]
    [TestCase("2.4.1", 4)]
    [TestCase("2.5.0", 5)]
    [TestCase("2.6.0", 6)]
    [TestCase("2.7.2", 7)]
    [TestCase("2.8.1", 8)]
    [TestCase("2.9.0", 9)]
    public void RequireAbi_RejectsAnOlderMinor_NamingTheMinorItNeeds(string version, int minor)
    {
        var ex = Should.Throw<DdbAbiMismatchException>(() => DdbAbi.RequireAbi(version, Abi(2, minor)));

        ex.Message.ShouldContain($"libddb {version} has ABI 2.{minor}");
        ex.Message.ShouldContain("(ABI 2.x with x >= 10)");
    }

    [TestCase(1, 0)]
    [TestCase(1, 1)]
    [TestCase(1, 5)]
    [TestCase(3, 0)]
    [TestCase(0, 10)]
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
        DdbAbi.Decode(Abi(2, 10)).ShouldBe((2, 10));
        DdbAbi.Major.ShouldBe(2);
        DdbAbi.Minor.ShouldBe(10);
    }
}
