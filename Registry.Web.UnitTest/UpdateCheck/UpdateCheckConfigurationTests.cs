using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Storage;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Registry.Web.Hosting;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Initialization;
using Registry.Web.Services.Import;
using Registry.Web.Services.Ports;
using Registry.Web.Services.UpdateCheck;
using Registry.Web.Utilities;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.UpdateCheck;

/// <summary>
/// The settings of the update check (<c>AppSettings:UpdateCheckCron</c>, <c>UpdateCheckEndpoint</c>,
/// <c>UpdateCheckEmail</c>), the recurring job they schedule, and the time of the daily check.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UpdateCheckConfigurationTests"
/// </summary>
[TestFixture]
public class UpdateCheckConfigurationTests
{
    private static IReadOnlyList<string> Validate(params (string Key, string Value)[] settings) =>
        RegistryOptions.Validate(
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings.ToDictionary(s => "AppSettings:" + s.Key, s => (string?)s.Value))
                .Build(),
            InstanceType.WebServer);

    [Test]
    public void Defaults_AreValid()
    {
        Validate().Where(f => f.Contains("UpdateCheck", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [TestCase("http://dronedb.app/update-check")]
    [TestCase("ftp://dronedb.app/update-check")]
    [TestCase("/wp-json/update-check")]
    [TestCase("dronedb.app/update-check")]
    [TestCase("")]
    public void AnEndpointThatIsNotAnAbsoluteHttpsUrl_StopsTheStart(string endpoint)
    {
        Validate(("UpdateCheckEndpoint", endpoint)).ShouldContain(f => f.Contains("AppSettings:UpdateCheckEndpoint", StringComparison.Ordinal));
    }

    [Test]
    public void AnHttpsEndpoint_OnAPrivateNetwork_IsAccepted()
    {
        Validate(("UpdateCheckEndpoint", "https://staging.internal:8443/update-check"))
            .Where(f => f.Contains("UpdateCheck", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [TestCase("weekly")]
    [TestCase("true")]
    [TestCase("")]
    public void AnUnknownEmailMode_StopsTheStart(string mode)
    {
        Validate(("UpdateCheckEmail", mode)).ShouldContain(f => f.Contains("AppSettings:UpdateCheckEmail", StringComparison.Ordinal));
    }

    [TestCase("security", UpdateCheckEmailMode.Security)]
    [TestCase("All", UpdateCheckEmailMode.All)]
    [TestCase(" off ", UpdateCheckEmailMode.Off)]
    public void TheEmailMode_IsReadInAnyCase(string value, UpdateCheckEmailMode expected)
    {
        Validate(("UpdateCheckEmail", value)).Where(f => f.Contains("UpdateCheck", StringComparison.Ordinal)).ShouldBeEmpty();
        new UpdateCheckOptions { UpdateCheckEmail = value }.EmailMode.ShouldBe(expected);
    }

    [TestCase(null, true)]
    [TestCase("", true)]
    [TestCase("15 3 * * *", true)]
    [TestCase("disabled", false)]
    [TestCase("OFF", false)]
    [TestCase(" none ", false)]
    public void Enabled_IsFalseOnlyForADisabledCron(string? cron, bool enabled) =>
        new UpdateCheckOptions { UpdateCheckCron = cron }.Enabled.ShouldBe(enabled);

    [Test]
    public void TheHttpClient_FollowsNoRedirect_TakesTenSeconds_AndIsNotTheGuardedOneOfTheImports()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddUpdateCheck();
        using var provider = services.BuildServiceProvider();

        var chain = new List<HttpMessageHandler>();
        for (var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(UpdateCheckClient.HttpClientName);
             handler != null; handler = (handler as DelegatingHandler)?.InnerHandler)
            chain.Add(handler);

        chain.OfType<SocketsHttpHandler>().ShouldHaveSingleItem().AllowAutoRedirect.ShouldBeFalse();
        provider.GetRequiredService<IHttpClientFactory>().CreateClient(UpdateCheckClient.HttpClientName)
            .Timeout.ShouldBe(TimeSpan.FromSeconds(10));
        UpdateCheckClient.HttpClientName.ShouldNotBe(SsrfHttpHandler.HttpClientName);
    }

    [Test]
    public void RecurringJobIds_OwnTheUpdateCheck()
    {
        RecurringJobIds.CheckUpdates.ShouldBe("check-updates");
        RecurringJobIds.Owned.ShouldContain(RecurringJobIds.CheckUpdates);
    }

    [Test]
    public void InstanceDailyCron_IsDeterministic_PerInstance()
    {
        var id = "a1b2c3d4-5e6f-4a7b-8c9d-0e1f2a3b4c5d";

        HangfireJobsInitializer.InstanceDailyCron(id).ShouldBe(HangfireJobsInitializer.InstanceDailyCron(id));
        HangfireJobsInitializer.InstanceDailyCron(id.ToUpperInvariant()).ShouldBe(HangfireJobsInitializer.InstanceDailyCron(id));
        HangfireJobsInitializer.InstanceDailyCron(id).ShouldNotBe(HangfireJobsInitializer.InstanceDailyCron(Guid.NewGuid().ToString()));
    }

    [Test]
    public void InstanceDailyCron_IsOnceADay_AtAValidTime_SpreadOverTheWholeDay()
    {
        var times = Enumerable.Range(0, 5000)
            .Select(i => HangfireJobsInitializer.InstanceDailyCron(new Guid(i, 0, 0, new byte[8]).ToString()))
            .ToList();

        var parsed = times.Select(c => c.Split(' ')).ToList();
        parsed.All(p => p.Length == 5 && p[2] == "*" && p[3] == "*" && p[4] == "*").ShouldBeTrue();
        var minutes = parsed.Select(p => (Hour: int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), Minute: int.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture))).ToList();
        minutes.All(t => t.Hour is >= 0 and < 24 && t.Minute is >= 0 and < 60).ShouldBeTrue();

        // Every hour of the day is used, none holds much more than its share (5000 / 24 = 208)
        var perHour = minutes.GroupBy(t => t.Hour).ToDictionary(g => g.Key, g => g.Count());
        perHour.Count.ShouldBe(24);
        perHour.Values.All(count => count is > 120 and < 300).ShouldBeTrue();
        minutes.Select(t => t.Minute).Distinct().Count().ShouldBe(60);
    }

    private static async Task<InMemoryStorage> Schedule(UpdateCheckOptions options)
    {
        var storage = new InMemoryStorage();
        await new HangfireJobsInitializer(new RecurringJobManager(storage),
                Microsoft.Extensions.Options.Options.Create(new JobScheduleOptions()), Microsoft.Extensions.Options.Options.Create(options), new FixedInstanceIdentity(),
                NullLogger<HangfireJobsInitializer>.Instance)
            .ExecuteAsync(CancellationToken.None);
        return storage;
    }

    private static RecurringJobDto? UpdateJob(InMemoryStorage storage)
    {
        using var connection = storage.GetConnection();
        return connection.GetRecurringJobs().SingleOrDefault(j => j.Id == RecurringJobIds.CheckUpdates);
    }

    [Test]
    public async Task ByDefault_TheJobRunsOnceADay_AtTheTimeOfTheInstance()
    {
        using var storage = await Schedule(new UpdateCheckOptions());

        var job = UpdateJob(storage).ShouldNotBeNull();
        job.Cron.ShouldBe(HangfireJobsInitializer.InstanceDailyCron(FixedInstanceIdentity.DefaultId));
        job.Job.Type.ShouldBe(typeof(IUpdateCheckService));
        job.Job.Method.Name.ShouldBe(nameof(IUpdateCheckService.CheckForUpdatesAsync));
        job.Job.Method.GetParameters().Select(p => p.ParameterType).ShouldContain(typeof(CancellationToken));
    }

    [Test]
    public async Task AnExplicitCron_WinsOverTheTimeOfTheInstance()
    {
        using var storage = await Schedule(new UpdateCheckOptions { UpdateCheckCron = "5 4 * * 1" });

        UpdateJob(storage).ShouldNotBeNull().Cron.ShouldBe("5 4 * * 1");
    }

    [TestCase("disabled")]
    [TestCase("off")]
    [TestCase("none")]
    public async Task ADisabledCron_RemovesTheJob(string cron)
    {
        using var storage = await Schedule(new UpdateCheckOptions());
        UpdateJob(storage).ShouldNotBeNull();

        // The same storage at the next start, with the check switched off
        await new HangfireJobsInitializer(new RecurringJobManager(storage),
                Microsoft.Extensions.Options.Options.Create(new JobScheduleOptions()), Microsoft.Extensions.Options.Options.Create(new UpdateCheckOptions { UpdateCheckCron = cron }),
                new FixedInstanceIdentity(), NullLogger<HangfireJobsInitializer>.Instance)
            .ExecuteAsync(CancellationToken.None);

        UpdateJob(storage).ShouldBeNull();
    }

    [Test]
    public async Task TheJobSurvivesTheSweepOfExpiredRecurringJobs()
    {
        using var storage = await Schedule(new UpdateCheckOptions());
        var time = new FakeTime(DateTime.UtcNow.AddDays(400));

        new Registry.Web.Services.Jobs.ExpiredRecurringJobsSweep(storage, time,
            NullLogger<Registry.Web.Services.Jobs.ExpiredRecurringJobsSweep>.Instance).Run(null, CancellationToken.None);

        UpdateJob(storage).ShouldNotBeNull();
    }
}
