using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Registry.Web.Hosting;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Initialization;
using Registry.Web.Services.UpdateCheck;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.UpdateCheck;

/// <summary>
/// The start-up task of the instance identity: where it runs, and what it tells the administrator (the disclosure of what
/// the update check sends, or that it is off).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~InstanceIdInitializerTests"
/// </summary>
[TestFixture]
public class InstanceIdInitializerTests
{
    private static async Task<RecordingLoggerFactory> Run(UpdateCheckOptions options)
    {
        var logs = new RecordingLoggerFactory();
        await new InstanceIdInitializer(new FixedInstanceIdentity(), Microsoft.Extensions.Options.Options.Create(options), logs)
            .ExecuteAsync(CancellationToken.None);
        return logs;
    }

    [Test]
    public void Order_IsAfterTheCaches_AndBeforeTheRecurringJobs()
    {
        var identity = StartupTaskOrder.InstanceIdentity;

        identity.ShouldBeGreaterThan(StartupTaskOrder.Caches);
        identity.ShouldBeLessThan(StartupTaskOrder.RecurringJobs);
        new InstanceIdInitializer(new FixedInstanceIdentity(), Microsoft.Extensions.Options.Options.Create(new UpdateCheckOptions()), new RecordingLoggerFactory())
            .Order.ShouldBe(identity);
    }

    [Test]
    public async Task Enabled_LogsTheDisclosure_WithTheInstanceId_AtInformationLevel()
    {
        var logs = await Run(new UpdateCheckOptions());

        var entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldBe(UpdateCheckDisclosure.For(FixedInstanceIdentity.DefaultId));
        entry.Message.ShouldContain($"instance ID ({FixedInstanceIdentity.DefaultId})");
        entry.Message.ShouldContain("Opt out: UpdateCheckCron=\"disabled\"");
    }

    [Test]
    public async Task TheDisclosure_IsLoggedInTheStartupCategory_WhichIsAlwaysWritten()
    {
        var logs = await Run(new UpdateCheckOptions());

        logs.Entries.Single().Category.ShouldBe(RegistryHost.StartupLogCategory);
    }

    [TestCase("disabled")]
    [TestCase("OFF")]
    [TestCase("none")]
    public async Task Disabled_LogsThatTheCheckIsOff_AndNotTheDisclosure(string cron)
    {
        var logs = await Run(new UpdateCheckOptions { UpdateCheckCron = cron });

        var entry = logs.Entries.ShouldHaveSingleItem();
        entry.Message.ShouldBe("Update check: disabled");
        entry.Level.ShouldBe(LogLevel.Information);
    }

    [Test]
    public void TheReadme_ShowsTheDisclosure_AsTheLogWritesIt()
    {
        var readme = File.ReadAllText(RepositoryFiles.PathOf("README.md"));

        readme.ShouldContain(UpdateCheckDisclosure.Template.Replace(UpdateCheckDisclosure.InstanceIdPlaceholder, "`<instance id>`", StringComparison.Ordinal));
    }

    [Test]
    public void TheDisclosure_HasOneDefinition_WithThePlaceholder()
    {
        UpdateCheckDisclosure.Template.ShouldContain(UpdateCheckDisclosure.InstanceIdPlaceholder);
        UpdateCheckDisclosure.For("x").ShouldNotContain(UpdateCheckDisclosure.InstanceIdPlaceholder);
        UpdateCheckDisclosure.Template.ShouldContain("docs.dronedb.app/docs/registry/admin/update-check");
        UpdateCheckDisclosure.DocsUrl.ShouldBe("https://docs.dronedb.app/docs/registry/admin/update-check");
    }
}
