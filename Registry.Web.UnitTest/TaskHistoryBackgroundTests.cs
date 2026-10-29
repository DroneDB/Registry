using System;
using System.Linq;
using System.Threading.Tasks;
using Hangfire.Common;
using Moq;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.PlatformTasks;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The task list tells the rebuild of an entry made with an older layout (<c>background</c>) from a build somebody
/// asked for, so the Hub does not show a spinner for one that waits for its turn.
/// </summary>
[TestFixture]
public class TaskHistoryBackgroundTests
{
    private RegistryContext _db = null!;

    [SetUp]
    public void SetUp() => _db = TestDb.CreateContext<RegistryContext>();

    [TearDown]
    public void TearDown() => _db.Dispose();

    private void Add(string id, Func<string?> display)
    {
        _db.JobIndices.Add(new JobIndex
        {
            JobId = id, OrgSlug = "org", DsSlug = "ds", Path = id + ".obj", ToolId = "build", ToolVersion = "1",
            CurrentState = "Enqueued", CreatedAtUtc = DateTime.UtcNow, MethodDisplay = display()
        });
        _db.SaveChanges();
    }

    [Test]
    public async Task List_MarksTheBackgroundRebuildAndNoOtherJob()
    {
        Add("rebuild", () => Job.FromExpression<DdbJobs>(j => j.RebuildInBackground("org", Guid.Empty, "m.obj", null)).ToString());
        Add("build", () => Job.FromExpression<DdbJobs>(j => j.Build("org", Guid.Empty, "m.obj", false, null)).ToString());
        Add("legacy", () => null);
        var history = new TaskHistory(Mock.Of<IDatasetAccessResolver>(), new JobIndexQuery(_db),
            Mock.Of<IRecordCache>(), null!, null!, TestSettings.Options<ProcessingPlatformSettings>(new AppSettings()));

        // A filter bypasses the cache of the canonical page
        var page = await history.ListPageAsync("org", "ds", "build", null, 0, 50);

        page.Items.ToDictionary(t => t.TaskId, t => t.Background)
            .ShouldBe(new System.Collections.Generic.Dictionary<string, bool>
                { ["rebuild"] = true, ["build"] = false, ["legacy"] = false }, ignoreOrder: true);
    }
}
