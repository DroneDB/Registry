using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Managers;
using Registry.Web.Services.PlatformTasks;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-XC-06: the task records say why a task failed. The summaries carry <c>error</c> (the message of
/// <c>JobLogText.UserMessage</c>) next to <c>errorType</c>; the status's <c>error</c> is that message, or the type
/// name of a task that failed before the message was recorded.
/// </summary>
[TestFixture]
public class TaskHistoryErrorTests
{
    private const string Message = "The file is damaged or not a valid GeoTIFF";

    private RegistryContext _db = null!;

    [SetUp]
    public void SetUp() => _db = TestDb.CreateContext<RegistryContext>();

    [TearDown]
    public void TearDown() => _db.Dispose();

    private void AddTask(string id, string state, string? errorType, string? errorMessage)
    {
        _db.JobIndices.Add(new JobIndex
        {
            JobId = id, OrgSlug = "org", DsSlug = "ds", Path = "ortho.tif", ToolId = "raster-export",
            ToolVersion = "1", CurrentState = state, CreatedAtUtc = DateTime.UtcNow, ErrorType = errorType,
            ErrorMessage = errorMessage
        });
        _db.SaveChanges();
    }

    private TaskHistory History()
    {
        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.GetPendingBuildInfo()).Returns([]);
        var access = new Mock<IDatasetAccessResolver>();
        access.Setup(a => a.ResolveAsync("org", "ds", AccessType.Read, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DatasetAccess(
                new Dataset { Slug = "ds", Organization = new Organization { Slug = "org" } }, () => ddb.Object));
        return new TaskHistory(access.Object, new JobIndexQuery(_db), Mock.Of<IRecordCache>(),
            new TaskAuthorizer(access.Object, new JobIndexQuery(_db), Mock.Of<IAuthManager>()), null!,
            TestJobs.BuildTaskWaits(_db), TestSettings.Options<ProcessingPlatformSettings>(new AppSettings()));
    }

    [Test]
    public async Task List_CarriesTheMessageNextToTheType()
    {
        AddTask("failed", "Failed", "DdbException", Message);
        AddTask("done", "Succeeded", null, null);

        var page = (await History().ListPageAsync("org", "ds", "raster-export", null, 0, 50)).Items;

        page.Single(t => t.TaskId == "failed").ShouldSatisfyAllConditions(
            t => t.Error.ShouldBe(Message),
            t => t.ErrorType.ShouldBe("DdbException"));
        page.Single(t => t.TaskId == "done").Error.ShouldBeNull();
    }

    [Test]
    public async Task Status_ErrorIsTheMessage_OrTheTypeOfAnOlderFailure()
    {
        AddTask("failed", "Failed", "DdbException", Message);
        AddTask("older", "Failed", "InvalidOperationException", null);

        (await History().GetStatusAsync("org", "ds", "failed")).Error.ShouldBe(Message);
        (await History().GetStatusAsync("org", "ds", "older")).Error.ShouldBe("InvalidOperationException");
    }
}
