using System.Net;
using NUnit.Framework;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <see cref="ReservedAddresses"/>, the one rule that <c>SsrfGuard</c> (an import may not connect to it) and
/// <c>PublicHost</c> (it cannot be the public address of the deployment) share.
/// </summary>
[TestFixture]
public class ReservedAddressesTests
{
    [TestCase("0.0.0.0")]
    [TestCase("0.255.255.255")]
    [TestCase("10.0.0.1")]
    [TestCase("100.64.0.1")]
    [TestCase("100.127.255.254")]
    [TestCase("127.0.0.1")]
    [TestCase("127.255.255.254")]
    [TestCase("169.254.169.254")]
    [TestCase("172.16.0.1")]
    [TestCase("172.31.255.255")]
    [TestCase("192.168.0.1")]
    [TestCase("198.18.0.1")]
    [TestCase("198.19.255.255")]
    [TestCase("224.0.0.1")]
    [TestCase("239.255.255.255")]
    [TestCase("240.0.0.1")]
    [TestCase("255.255.255.255")]
    [TestCase("::")]
    [TestCase("::1")]
    [TestCase("::7f00:1")]                   // IPv4-compatible loopback
    [TestCase("::ffff:127.0.0.1")]
    [TestCase("::ffff:100.64.0.1")]
    [TestCase("fe80::1")]
    [TestCase("febf::1")]
    [TestCase("fec0::1")]
    [TestCase("fc00::1")]
    [TestCase("fdff::1")]
    [TestCase("fd00:ec2::254")]              // the AWS IPv6 metadata address
    [TestCase("ff02::1")]
    [TestCase("64:ff9b::7f00:1")]            // NAT64 of 127.0.0.1
    [TestCase("64:ff9b::a9fe:a9fe")]         // NAT64 of 169.254.169.254
    [TestCase("2002:7f00:1::")]              // 6to4 of 127.0.0.1
    [TestCase("2002:a00:1::1")]              // 6to4 of 10.0.0.1
    [TestCase("2001:0:7f00:1::1")]           // Teredo whose server is 127.0.0.1
    [TestCase("2001:0:808:808::80ff:fffe")]  // Teredo whose client (inverted) is 127.0.0.1
    public void IsNonPublic_IsTrueForLocalPrivateAndReservedAddresses(string address) =>
        ReservedAddresses.IsNonPublic(IPAddress.Parse(address)).ShouldBeTrue();

    [TestCase("1.1.1.1")]
    [TestCase("8.8.8.8")]
    [TestCase("100.63.255.255")]
    [TestCase("100.128.0.1")]
    [TestCase("169.253.0.1")]
    [TestCase("172.15.0.1")]
    [TestCase("172.32.0.1")]
    [TestCase("198.17.0.1")]
    [TestCase("198.20.0.1")]
    [TestCase("223.255.255.254")]
    [TestCase("::ffff:8.8.8.8")]
    [TestCase("2606:4700:4700::1111")]
    [TestCase("2001:4860:4860::8888")]
    [TestCase("64:ff9b::808:808")]           // NAT64 of 8.8.8.8
    [TestCase("2002:808:808::")]             // 6to4 of 8.8.8.8
    [TestCase("2001:0:808:808::f7f7:f7f7")]  // Teredo of two public IPv4 addresses
    public void IsNonPublic_IsFalseForPublicAddresses(string address) =>
        ReservedAddresses.IsNonPublic(IPAddress.Parse(address)).ShouldBeFalse();
}
