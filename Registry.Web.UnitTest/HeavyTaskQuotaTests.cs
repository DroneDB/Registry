#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;
using Shouldly;
using Registry.Web.Test.Support;

namespace Registry.Web.Test;

/// <summary>
/// Tests for the <see cref="HeavyTaskQuota"/> concurrency limits: the per-tool per-user limit of the
/// <c>import-file</c> tool, and that only the active tasks of users take task slots (dataset builds and the
/// jobs of the automatic build service or of no user do not), on the real job index query.
/// </summary>
[TestFixture]
public class HeavyTaskQuotaTests
{
    private static readonly JsonElement EmptyParams = JsonDocument.Parse("{}").RootElement;

    private static HeavyTaskSubmitRequest Request(string toolId, string org = "org", string? userId = "user") =>
        new(org, "ds", toolId, "1", null, EmptyParams, false, userId, null);

    private static HeavyTaskSubmitRequest ImportFileRequest() => Request("import-file");

    private static HeavyToolPlan SmallPlan(string toolId = "import-file") => new(100, toolId, null, null);

    private static Mock<IJobIndexQuery> QueryWithImportFileCount(long importFileActive)
    {
        var query = new Mock<IJobIndexQuery>();
        // Global, per-org and per-user (queue) checks all pass.
        query.Setup(q => q.CountActiveUserTasksAsync(null, null, null, It.IsAny<CancellationToken>())).ReturnsAsync(0);
        query.Setup(q => q.CountActiveUserTasksAsync("org", null, null, It.IsAny<CancellationToken>())).ReturnsAsync(0);
        query.Setup(q => q.CountActiveUserTasksAsync(null, "user", null, It.IsAny<CancellationToken>())).ReturnsAsync(0);
        // Per-tool per-user count for import-file.
        query.Setup(q => q.CountActiveUserTasksAsync(null, "user", "import-file", It.IsAny<CancellationToken>()))
            .ReturnsAsync(importFileActive);
        return query;
    }

    private static HeavyTaskQuota QuotaWith(IJobIndexQuery query, Action<ProcessingPlatformSettings>? configure = null)
    {
        var platform = new ProcessingPlatformSettings();
        configure?.Invoke(platform);
        var settings = new AppSettings { ProcessingPlatform = platform };
        return new HeavyTaskQuota(query,
            TestSettings.Options<ProcessingPlatformSettings>(Microsoft.Extensions.Options.Options.Create(settings)));
    }

    [Test]
    public async Task ImportFile_UnderLimit_IsAllowed()
    {
        var quota = QuotaWith(QueryWithImportFileCount(1).Object, s => s.MaxConcurrentUrlImportsPerUser = 2);

        var result = await quota.EvaluateAsync(ImportFileRequest(), SmallPlan());

        result.IsAllowed.ShouldBeTrue();
    }

    [Test]
    public async Task ImportFile_AtLimit_IsRejectedWith429()
    {
        var quota = QuotaWith(QueryWithImportFileCount(2).Object, s => s.MaxConcurrentUrlImportsPerUser = 2);

        var result = await quota.EvaluateAsync(ImportFileRequest(), SmallPlan());

        result.IsAllowed.ShouldBeFalse();
        result.Code.ShouldBe(HeavyTaskQuotaCode.Exceeded);
    }

    #region On the job index

    private static int _nextId;

    private static JobIndex Job(string state, string toolId, string? userId, string org = "org") => new()
    {
        JobId = Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture),
        OrgSlug = org,
        DsSlug = "ds",
        ToolId = toolId,
        ToolVersion = "1",
        CurrentState = state,
        UserId = userId,
        CreatedAtUtc = DateTime.UtcNow,
        LastStateChangeUtc = DateTime.UtcNow
    };

    private static IEnumerable<JobIndex> Jobs(int count, string state, string toolId, string? userId,
        string org = "org") => Enumerable.Range(0, count).Select(_ => Job(state, toolId, userId, org));

    /// <summary>The active jobs that are not tasks of a user, many more than any limit allows.</summary>
    private static IEnumerable<JobIndex> NonUserJobs() =>
    [
        // Retry coordinators of pending builds, which wait in Scheduled.
        ..Jobs(100, "Scheduled", HeavyToolIds.Build, MagicStrings.AutoBuildServiceUserId),
        // Builds the user started by uploading files, in the org of the request.
        ..Jobs(40, "Enqueued", HeavyToolIds.Build, "user"),
        ..Jobs(10, "Processing", HeavyToolIds.Build, "user"),
        ..Jobs(10, "Indexing", HeavyToolIds.Build, "other-user", "other-org"),
        // Heavy tasks the automatic build service or the system submitted without a user.
        ..Jobs(40, "Enqueued", HeavyToolIds.RescanIndex, MagicStrings.AutoBuildServiceUserId),
        ..Jobs(40, "Enqueued", HeavyToolIds.RescanIndex, null),
        ..Jobs(40, "Enqueued", HeavyToolIds.RescanIndex, "")
    ];

    private static async Task<HeavyTaskQuotaResult> EvaluateOnIndexAsync(HeavyTaskSubmitRequest request,
        IEnumerable<JobIndex> rows, Action<ProcessingPlatformSettings>? configure = null)
    {
        await using var db = TestDb.CreateContext<RegistryContext>(ctx =>
        {
            ctx.JobIndices.AddRange(rows);
            ctx.SaveChanges();
        });
        return await QuotaWith(new JobIndexQuery(db), configure)
            .EvaluateAsync(request, SmallPlan(request.ToolId));
    }

    [Test]
    public async Task BulkDownload_IsAllowed_WhenBuildsAndServiceJobsAreActive()
    {
        var result = await EvaluateOnIndexAsync(Request("bulk-download"), NonUserJobs());

        result.IsAllowed.ShouldBeTrue(result.Message);
    }

    [Test]
    public async Task ImportFile_IsAllowed_WhenBuildsAndServiceJobsAreActive()
    {
        var result = await EvaluateOnIndexAsync(Request("import-file"), NonUserJobs());

        result.IsAllowed.ShouldBeTrue(result.Message);
    }

    [Test]
    public async Task GlobalLimit_CountsActiveUserTasks()
    {
        // Spread over orgs and users so that only the global limit is reached.
        var rows = Enumerable.Range(0, 32)
            .Select(i => Job(i % 2 == 0 ? "Processing" : "Enqueued", "raster-export", $"u{i}", $"org{i}"))
            .Concat(NonUserJobs());

        var result = await EvaluateOnIndexAsync(Request("bulk-download", org: "fresh-org", userId: "fresh-user"), rows);

        result.IsAllowed.ShouldBeFalse();
        result.Code.ShouldBe(HeavyTaskQuotaCode.Exceeded);
        result.Message!.ShouldContain("maximum task concurrency");
    }

    [Test]
    public async Task GlobalLimit_IgnoresFinishedUserTasks()
    {
        var rows = Jobs(16, "Succeeded", "raster-export", "u1").Concat(Jobs(16, "Failed", "raster-export", "u2"));

        var result = await EvaluateOnIndexAsync(Request("bulk-download"), rows, s => s.MaxConcurrentTasksGlobal = 1);

        result.IsAllowed.ShouldBeTrue(result.Message);
    }

    [Test]
    public async Task OrgLimit_CountsActiveUserTasksOfTheOrg()
    {
        var rows = Enumerable.Range(0, 10).Select(i => Job("Enqueued", "raster-export", $"u{i}"))
            .Concat(NonUserJobs());

        var result = await EvaluateOnIndexAsync(Request("bulk-download", userId: "fresh-user"), rows);

        result.IsAllowed.ShouldBeFalse();
        result.Message!.ShouldContain("organization");
    }

    [Test]
    public async Task UserLimit_CountsActiveTasksOfTheUser()
    {
        // In several orgs, so that only the limit of the user is reached.
        var rows = Enumerable.Range(0, 3).Select(i => Job("Processing", "raster-export", "user", $"org{i}"))
            .Concat(NonUserJobs());

        var result = await EvaluateOnIndexAsync(Request("bulk-download"), rows, s => s.MaxQueuedTasksPerUser = 3);

        result.IsAllowed.ShouldBeFalse();
        result.Message!.ShouldContain("too many queued or running tasks");
    }

    [Test]
    public async Task BulkDownloadLimit_CountsActiveBulkDownloadsOfTheUser()
    {
        var rows = new[] { Job("Processing", "bulk-download", "user") }
            .Concat(Jobs(3, "Succeeded", "bulk-download", "user"))
            .Concat(NonUserJobs());

        var result = await EvaluateOnIndexAsync(Request("bulk-download"), rows);

        result.IsAllowed.ShouldBeFalse();
        result.Message!.ShouldContain("active download archive");
    }

    [Test]
    public async Task ImportFileLimit_CountsActiveUrlImportsOfTheUser()
    {
        var rows = Jobs(2, "Enqueued", "import-file", "user").Concat(NonUserJobs());

        var result = await EvaluateOnIndexAsync(Request("import-file"), rows);

        result.IsAllowed.ShouldBeFalse();
        result.Message!.ShouldContain("active URL imports");
    }

    #endregion
}
