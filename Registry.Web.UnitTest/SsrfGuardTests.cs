using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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

    [TestCase("localhost", "This server cannot import from local or private network addresses (localhost). Use the public address of the registry.")]
    [TestCase("10.1.2.3", "This server cannot import from local or private network addresses (10.1.2.3). Use the public address of the registry.")]
    public async Task AssertAllowed_RefusesALocalAddress_WithAPlainMessage_AndLogsTheResolvedAddress(string host, string message)
    {
        var logger = new CapturingLogger();

        var ex = await Should.ThrowAsync<ArgumentException>(
            async () => await new SsrfGuard(new ImportSettings(), logger).AssertAllowedAsync(host, CancellationToken.None));

        ex.Message.ShouldBe(message);
        ex.Message.ShouldNotContain("reserved");
        // The address the host resolved to is in the log, not in the message
        var logged = logger.Warnings.ShouldHaveSingleItem();
        logged.ShouldContain(host);
        logged.ShouldMatch(@"10\.1\.2\.3|127\.0\.0\.1|::1");
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

    private sealed class CapturingLogger : ILogger<SsrfGuard>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
