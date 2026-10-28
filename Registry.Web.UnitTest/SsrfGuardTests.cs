using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Import;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Tests for <see cref="SsrfGuard"/>, the security-critical guard that blocks outbound import
/// requests to private/loopback/metadata addresses. IP literals are used so no real DNS lookups occur.
/// </summary>
[TestFixture]
public class SsrfGuardTests
{
    private static SsrfGuard Guard(ImportSettings? settings = null) => new(settings ?? new ImportSettings());

    [TestCase("127.0.0.1")]
    [TestCase("10.1.2.3")]
    [TestCase("192.168.1.1")]
    [TestCase("172.16.0.1")]
    [TestCase("169.254.169.254")]
    [TestCase("::1")]                       // IPv6 loopback
    [TestCase("fc00::1")]                    // IPv6 ULA (fc00::/7)
    [TestCase("fd00::1")]                    // IPv6 ULA (fd00::/8)
    [TestCase("::ffff:10.0.0.1")]            // IPv4-mapped private
    [TestCase("::ffff:127.0.0.1")]           // IPv4-mapped loopback
    [TestCase("::ffff:169.254.169.254")]     // IPv4-mapped cloud metadata
    public async Task AssertAllowed_BlocksPrivateAndReservedAddresses(string host)
    {
        await Should.ThrowAsync<ArgumentException>(
            async () => await Guard().AssertAllowedAsync(host, CancellationToken.None));
    }

    [Test]
    public async Task AssertAllowed_AllowsPublicAddress()
    {
        await Should.NotThrowAsync(
            async () => await Guard().AssertAllowedAsync("8.8.8.8", CancellationToken.None));
    }

    [Test]
    public async Task AssertAllowed_EmptyHost_Throws()
    {
        await Should.ThrowAsync<ArgumentException>(
            async () => await Guard().AssertAllowedAsync("", CancellationToken.None));
    }

    [Test]
    public async Task AssertAllowed_PrivateNetworksToggle_AllowsPrivate()
    {
        var settings = new ImportSettings { SsrfAllowPrivateNetworks = true };
        await Should.NotThrowAsync(
            async () => await Guard(settings).AssertAllowedAsync("10.1.2.3", CancellationToken.None));
    }

    [Test]
    public async Task AssertAllowed_AllowedHostsList_AllowsListedPrivateHost()
    {
        var settings = new ImportSettings { SsrfAllowedHosts = ["10.1.2.3"] };
        await Should.NotThrowAsync(
            async () => await Guard(settings).AssertAllowedAsync("10.1.2.3", CancellationToken.None));
    }
}
