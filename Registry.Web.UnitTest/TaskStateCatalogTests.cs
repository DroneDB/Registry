using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Shouldly;
using Registry.Web.Test.Support;

namespace Registry.Web.Test;

/// <summary>Indexing in the task state catalog, deduplication, active counts and quotas.</summary>
[TestFixture]
public class TaskStateCatalogTests
{
    private PublicationEnv _env = null!;
    private JobIndexQuery _query = null!;
    private static readonly DateTime Created = DateTime.UtcNow.AddMinutes(-3);

    [SetUp]
    public void SetUp()
    {
        _env = new PublicationEnv();
        _query = new JobIndexQuery(_env.Db);
    }

    [TearDown]
    public void TearDown() => _env.Dispose();

    [Test]
    public void Indexing_IsActive_AndInAll_ButNotTerminal()
    {
        TaskStateCatalog.Active.ShouldContain("Indexing");
        TaskStateCatalog.All.ShouldContain("Indexing");
        TaskStateCatalog.Terminal.ShouldNotContain("Indexing");
        TaskStateCatalog.IsTerminal("Indexing").ShouldBeFalse();
        TaskStateCatalog.Terminal.ShouldBe(["Succeeded", "Failed", "Deleted"]);
    }

    [Test]
    public void Catalog_StateNameMatchesTheIndexingState() => IndexingState.StateName.ShouldBe("Indexing");

    [Test]
    public void All_IsActiveThenTerminal_WithoutDuplicates()
    {
        TaskStateCatalog.All.ShouldBe(TaskStateCatalog.Active.Concat(TaskStateCatalog.Terminal).ToArray());
        TaskStateCatalog.All.Distinct().Count().ShouldBe(TaskStateCatalog.All.Length);
    }

    [Test]
    public async Task Dedup_ReturnsAnActiveIndexingRow()
    {
        var p = PublicationEnv.Payload(requestHash: "dedup-1");
        _env.SeedRow("11", Created, "Indexing", p);

        var found = await _query.FindDedupCandidateAsync("org", "ds", "build", "dedup-1", 24);

        found.ShouldNotBeNull();
        found!.JobId.ShouldBe("11");
        found.CurrentState.ShouldBe("Indexing");
    }

    [Test]
    public async Task Dedup_PrefersNewestAmongActiveRows_AndIgnoresFailedAndDeleted()
    {
        var p = PublicationEnv.Payload(requestHash: "dedup-2");
        _env.SeedRow("1", Created.AddMinutes(-30), "Failed", p);
        _env.SeedRow("2", Created.AddMinutes(-20), "Deleted", p);
        (await _query.FindDedupCandidateAsync("org", "ds", "build", "dedup-2", 24)).ShouldBeNull();

        _env.SeedRow("3", Created, "Indexing", p);
        (await _query.FindDedupCandidateAsync("org", "ds", "build", "dedup-2", 24))!.JobId.ShouldBe("3");
    }

    [Test]
    public async Task ActiveUserTaskCount_CountsIndexing_ScopedByOrgUserAndTool()
    {
        _env.SeedRow("1", Created, "Indexing", PublicationEnv.Payload(tool: "tool-x"));
        _env.SeedRow("2", Created, "Enqueued", PublicationEnv.Payload(tool: "tool-y"));
        _env.SeedRow("3", Created, "Succeeded", PublicationEnv.Payload(tool: "tool-x"));
        _env.SeedRow("4", Created, "Indexing", PublicationEnv.Payload(org: "other", tool: "tool-x"));
        _env.SeedRow("5", Created, "Indexing", PublicationEnv.Payload(tool: "build"));

        (await _query.CountActiveUserTasksAsync()).ShouldBe(3);
        (await _query.CountActiveUserTasksAsync("org")).ShouldBe(2);
        (await _query.CountActiveUserTasksAsync(userId: "user-1", toolId: "tool-x")).ShouldBe(2);
        (await _query.CountActiveUserTasksAsync("org", toolId: "tool-y")).ShouldBe(1);
    }

    [Test]
    public async Task Quota_CountsIndexingTasks_GlobalAndPerTool()
    {
        _env.SeedRow("1", Created, "Indexing", PublicationEnv.Payload(tool: "import-file"));
        var request = new HeavyTaskSubmitRequest("org", "ds", "import-file", "1", null,
            System.Text.Json.JsonDocument.Parse("{}").RootElement, false, "user-1", null);
        var plan = new HeavyToolPlan(100, "import-file", null, null);

        HeavyTaskQuota Quota(Action<ProcessingPlatformSettings> configure)
        {
            var settings = new ProcessingPlatformSettings
            {
                MaxConcurrentTasksGlobal = 100, MaxConcurrentTasksPerOrg = 100, MaxQueuedTasksPerUser = 100,
                MaxConcurrentUrlImportsPerUser = 100
            };
            configure(settings);
            return new HeavyTaskQuota(_query, TestSettings.Options<ProcessingPlatformSettings>(Microsoft.Extensions.Options.Options.Create(new AppSettings { ProcessingPlatform = settings })));
        }

        (await Quota(_ => { }).EvaluateAsync(request, plan)).IsAllowed.ShouldBeTrue();
        (await Quota(s => s.MaxConcurrentTasksGlobal = 1).EvaluateAsync(request, plan)).IsAllowed.ShouldBeFalse();
        (await Quota(s => s.MaxConcurrentTasksPerOrg = 1).EvaluateAsync(request, plan)).IsAllowed.ShouldBeFalse();
        (await Quota(s => s.MaxQueuedTasksPerUser = 1).EvaluateAsync(request, plan)).IsAllowed.ShouldBeFalse();
        (await Quota(s => s.MaxConcurrentUrlImportsPerUser = 1).EvaluateAsync(request, plan)).IsAllowed.ShouldBeFalse();
    }
}
