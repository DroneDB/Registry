using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Registry.Web.Hosting;
using Registry.Web.Models.Configuration;
using Shouldly;

namespace Registry.Web.Test.BuildNotifications;

/// <summary>
/// <c>AppSettings:BuildNotifications:DigestMinutes</c>: five minutes by default, one minute to one hour, checked at the
/// start of every host.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~BuildNotificationOptionsTests"
/// </summary>
[TestFixture]
public class BuildNotificationOptionsTests
{
    private const string Key = "AppSettings:BuildNotifications:DigestMinutes";

    private static List<string> Failures(string? digestMinutes, InstanceType role = InstanceType.WebServer)
    {
        var settings = new Dictionary<string, string?>();
        if (digestMinutes != null)
            settings[Key] = digestMinutes;
        return RegistryOptions.Validate(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), role)
            .Where(f => f.Contains("BuildNotifications", StringComparison.Ordinal)).ToList();
    }

    [Test]
    public void TheDefault_IsFiveMinutes_AndValid()
    {
        new ConfigurationBuilder().Build().GetRegistryOptions<BuildNotificationOptions>().DigestMinutes.ShouldBe(5);
        Failures(null).ShouldBeEmpty();
    }

    [TestCase("1")]
    [TestCase("60")]
    public void AWindowWithinTheRange_IsValid(string minutes)
    {
        Failures(minutes).ShouldBeEmpty();
        Failures(minutes, InstanceType.ProcessingNode).ShouldBeEmpty();
    }

    [TestCase("0")]
    [TestCase("61")]
    [TestCase("-5")]
    public void AWindowOutsideTheRange_StopsTheStart(string minutes)
    {
        Failures(minutes).ShouldBe([$"{Key} is not valid (must be between 1 and 60)"]);
    }
}
