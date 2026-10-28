#nullable disable
using NUnit.Framework;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>The listen address the command line accepts, and the "host:port" it is normalized to.</summary>
[TestFixture]
public class ProgramAddressTests
{
    [TestCase("localhost", "localhost:5000")]
    [TestCase("LOCALHOST", "LOCALHOST:5000")]
    [TestCase("127.0.0.1", "127.0.0.1:5000")]
    [TestCase("8080", "localhost:8080")]
    [TestCase("localhost:8080", "localhost:8080")]
    [TestCase("127.0.0.1:1", "127.0.0.1:1")]
    [TestCase("localhost:65535", "localhost:65535")]
    public void TryNormalizeAddress_ValidAddress_IsNormalizedToHostAndPort(string address, string expected)
    {
        Program.TryNormalizeAddress(address, out var normalized, out var error).ShouldBeTrue(error);

        normalized.ShouldBe(expected);
    }

    [TestCase("0")]
    [TestCase("65536")]
    [TestCase("localhost:0")]
    [TestCase("localhost:65536")]
    [TestCase("localhost:abc")]
    [TestCase("localhost:")]
    public void TryNormalizeAddress_InvalidPort_IsRefused(string address)
    {
        Program.TryNormalizeAddress(address, out _, out var error).ShouldBeFalse();

        error.ShouldBe("Invalid port");
    }

    [TestCase("example.com")]
    [TestCase("example.com:8080")]
    [TestCase("a:1:2")]
    public void TryNormalizeAddress_InvalidHost_IsRefused(string address)
    {
        Program.TryNormalizeAddress(address, out _, out var error).ShouldBeFalse();

        error.ShouldBe("Invalid address");
    }
}
