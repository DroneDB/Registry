using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Registry.Web.Data.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test.Support;

/// <summary>Set-ups of <see cref="IJobIndexQuery"/> mocks over job index rows.</summary>
public static class JobIndexQueryMocks
{
    /// <summary>
    /// Answers <see cref="IJobIndexQuery.GetLatestJobsByPathAsync"/> for the dataset as the real query does: the
    /// newest row of the tool for each asked path that has one. <paramref name="rows"/> is read at each call, so a test
    /// that adds rows while it runs is seen.
    /// </summary>
    public static Mock<IJobIndexQuery> WithLatestJobsFrom(this Mock<IJobIndexQuery> index, string orgSlug, string dsSlug,
        Func<IEnumerable<JobIndex>> rows)
    {
        ArgumentNullException.ThrowIfNull(index);
        index.Setup(x => x.GetLatestJobsByPathAsync(orgSlug, dsSlug, It.IsAny<string>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, string toolId, IReadOnlyCollection<string> paths, CancellationToken _) =>
                Task.FromResult<IReadOnlyDictionary<string, LatestJob>>(rows()
                    .Where(j => j.ToolId == toolId && j.Path != null && paths.Contains(j.Path))
                    .GroupBy(j => j.Path!)
                    .ToDictionary(g => g.Key, g =>
                    {
                        var latest = g.MaxBy(j => j.CreatedAtUtc)!;
                        return new LatestJob(latest.JobId, latest.CurrentState, latest.MethodDisplay);
                    })));
        return index;
    }

    /// <inheritdoc cref="WithLatestJobsFrom(Mock{IJobIndexQuery},string,string,Func{IEnumerable{JobIndex}})"/>
    public static Mock<IJobIndexQuery> WithLatestJobsFrom(this Mock<IJobIndexQuery> index, string orgSlug, string dsSlug,
        params JobIndex[] rows) => index.WithLatestJobsFrom(orgSlug, dsSlug, () => rows);

    /// <summary>Verifies that the whole history of a dataset was never read (<see cref="IJobIndexQuery.GetByOrgDsAsync"/>).</summary>
    public static void VerifyHistoryNeverRead(this Mock<IJobIndexQuery> index) =>
        index.Verify(x => x.GetByOrgDsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
}
