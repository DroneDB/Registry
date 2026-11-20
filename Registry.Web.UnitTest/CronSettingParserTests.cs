using System;
using System.Linq;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-ADM-06: the cron rule of <see cref="CronSetting"/> (the field counts and the macros the Hub checks) is what the
/// parser that schedules the setting reads. The tests hand the resolved value to the real
/// <see cref="RecurringJobManager"/> (Hangfire.Core, which holds Cronos), as <c>HangfireJobsInitializer</c> does.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~CronSettingParserTests"
/// </summary>
[TestFixture]
public class CronSettingParserTests
{
    private InMemoryStorage _storage = null!;

    [SetUp]
    public void SetUp() => _storage = new InMemoryStorage();

    [TearDown]
    public void TearDown() => _storage.Dispose();

    public static void Noop() { }

    /// <summary>Whether Hangfire schedules <paramref name="setting"/> as the initializer would (null: the job is removed).</summary>
    private bool Schedules(string setting)
    {
        var cron = CronSetting.Resolve(setting, "0 0 * * *");
        if (cron is null) return false;
        try
        {
            new RecurringJobManager(_storage).AddOrUpdate("probe", Job.FromExpression(() => Noop()), cron);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [TestCase("@yearly")]
    [TestCase("@annually")]
    [TestCase("@monthly")]
    [TestCase("@weekly")]
    [TestCase("@daily")]
    [TestCase("@midnight")]
    [TestCase("@hourly")]
    [TestCase("@every_minute")]
    [TestCase("@every_second")]
    [TestCase("@DAILY")]
    [TestCase("  @Hourly  ")]
    public void AMacro_IsScheduled_InAnyCaseAndWithSpacesAround(string setting) => Schedules(setting).ShouldBeTrue(setting);

    [Test]
    public void TheMacrosTheHubIsTold_AreTheNineHangfireReads()
    {
        CronSetting.Macros.ShouldBe(
            ["@yearly", "@annually", "@monthly", "@weekly", "@daily", "@midnight", "@hourly", "@every_minute", "@every_second"]);
        foreach (var macro in CronSetting.Macros)
            Schedules(macro).ShouldBeTrue(macro);
    }

    [TestCase("0 0 * * *")]
    [TestCase("*/5 * * * *")]
    [TestCase("0 0 0 * * *")]
    [TestCase("*/5 * * * * *")]
    [TestCase("  0   3 * *  1 ")]
    public void FiveOrSixFields_AreScheduled(string setting) => Schedules(setting).ShouldBeTrue(setting);

    [TestCase("*")]
    [TestCase("* *")]
    [TestCase("* * *")]
    [TestCase("0 0 * *")]
    [TestCase("0 0 0 0 * * *")]
    [TestCase("@reboot")]
    [TestCase("@daily extra")]
    [TestCase("daily")]
    [TestCase("every day please")]
    public void Anything_Else_IsNotScheduled(string setting) => Schedules(setting).ShouldBeFalse(setting);

    [Test]
    public void TheFieldCountsTheHubIsTold_AreTheOnesHangfireReads()
    {
        // n copies of "*": the count alone decides
        var accepted = Enumerable.Range(1, 8)
            .Where(n => Schedules(string.Join(' ', Enumerable.Repeat("*", n))))
            .ToArray();

        accepted.ShouldBe(Enumerable.Range(CronSetting.MinFields, CronSetting.MaxFields - CronSetting.MinFields + 1).ToArray());
        accepted.ShouldBe([5, 6]);
    }

    [TestCase("disabled")]
    [TestCase("OFF")]
    [TestCase(" None ")]
    public void ADisableWord_NeverReachesTheParser_TheJobIsRemoved(string setting)
    {
        CronSetting.Resolve(setting, "0 0 * * *").ShouldBeNull();
        Schedules(setting).ShouldBeFalse();
    }
}
