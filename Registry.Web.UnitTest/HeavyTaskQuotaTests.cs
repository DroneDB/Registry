#nullable enable
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Tests for the <see cref="HeavyTaskQuota"/> per-tool per-user concurrency limit for the
/// <c>import-file</c> tool (spec: bound concurrent server-side URL downloads per user).
/// </summary>
[TestFixture]
public class HeavyTaskQuotaTests
{
    private static readonly JsonElement EmptyParams = JsonDocument.Parse("{}").RootElement;

    private static HeavyTaskSubmitRequest ImportFileRequest() =>
        new("org", "ds", "import-file", "1", null, EmptyParams, false, "user", null);

    private static HeavyToolPlan SmallPlan() => new(100, "import-file", null, null);

    private static Mock<IJobIndexQuery> QueryWithImportFileCount(long importFileActive)
    {
        var query = new Mock<IJobIndexQuery>();
        // Global, per-org and per-user (queue) checks all pass.
        query.Setup(q => q.CountActiveAsync(null, null, null, It.IsAny<CancellationToken>())).ReturnsAsync(0);
        query.Setup(q => q.CountActiveAsync("org", null, null, It.IsAny<CancellationToken>())).ReturnsAsync(0);
        query.Setup(q => q.CountActiveAsync(null, "user", null, It.IsAny<CancellationToken>())).ReturnsAsync(0);
        // Per-tool per-user count for import-file.
        query.Setup(q => q.CountActiveAsync(null, "user", "import-file", It.IsAny<CancellationToken>()))
            .ReturnsAsync(importFileActive);
        return query;
    }

    private static HeavyTaskQuota QuotaWith(Mock<IJobIndexQuery> query, int maxUrlImportsPerUser = 2)
    {
        var settings = new AppSettings
        {
            ProcessingPlatform = new ProcessingPlatformSettings { MaxConcurrentUrlImportsPerUser = maxUrlImportsPerUser }
        };
        return new HeavyTaskQuota(query.Object, Microsoft.Extensions.Options.Options.Create(settings));
    }

    [Test]
    public async Task ImportFile_UnderLimit_IsAllowed()
    {
        var quota = QuotaWith(QueryWithImportFileCount(1), maxUrlImportsPerUser: 2);

        var result = await quota.EvaluateAsync(ImportFileRequest(), SmallPlan());

        result.IsAllowed.ShouldBeTrue();
    }

    [Test]
    public async Task ImportFile_AtLimit_IsRejectedWith429()
    {
        var quota = QuotaWith(QueryWithImportFileCount(2), maxUrlImportsPerUser: 2);

        var result = await quota.EvaluateAsync(ImportFileRequest(), SmallPlan());

        result.IsAllowed.ShouldBeFalse();
        result.Code.ShouldBe(HeavyTaskQuotaCode.Exceeded);
    }
}
