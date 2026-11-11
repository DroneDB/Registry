using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Configuration;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-ADM-13: a cron field of the admin configuration view carries the schedule its job runs when the field is
/// empty (<see cref="JobScheduleOptions.DefaultCrons"/>) as its default, so the Hub shows "Default: 0 0 * * *" and
/// the field is not "Not set".
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~ConfigurationCronDefaultsTests"
/// </summary>
[TestFixture]
public class ConfigurationCronDefaultsTests
{
    [Test]
    public void TheCronFieldsOfTheCronJobsSection_CarryTheDefaultCronsAsTheirDefault()
    {
        using var factory = new RegistryWebFactory();
        using var scope = factory.Services.CreateScope();
        var section = scope.ServiceProvider.GetServices<IConfigurationSectionBuilder>()
            .SelectMany(b => b.Build())
            .Select(p => p.Section)
            .Single(s => s.Name == "CronJobs");

        var crons = section.Fields.ShouldNotBeNull().Where(f => f.FieldType == "cron")
            .ToDictionary(f => f.Key!, f => f.DefaultValue);

        crons.ShouldBe(new System.Collections.Generic.Dictionary<string, string?>
        {
            ["CleanupExpiredJobsCron"] = "0 0 * * *",
            ["SyncJobIndexStatesCron"] = "0 * * * *",
            ["RecoverIndexedJobPublicationsCron"] = "* * * * *",
            ["ProcessPendingBuildsCron"] = "0 */6 * * *",
            ["OrphanedDatasetCleanupCron"] = "0 3 * * *",
            ["DatasetCleanupCron"] = "0 0 * * *",
            ["ArtifactCompletenessCheckerCron"] = "0 2 * * *",
            ["JobIndexCleanupCron"] = "0 4 * * *"
        }, ignoreOrder: true);
    }
}
