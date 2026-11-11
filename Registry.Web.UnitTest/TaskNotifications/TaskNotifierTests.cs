using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.Email;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;
using Registry.Web.Services.TaskNotifications;
using Registry.Web.Services.Users;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.TaskNotifications;

/// <summary>
/// Who is told when a heavy task ends (FR-PRC-40, D-012), over a real outbox: the preference (none, failed, all), the tasks
/// that are nobody's to report, the user who cannot be e-mailed or can no longer read the dataset, the message the e-mail
/// carries (label, scrubbed error, duration), and one e-mail per run of a task.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~TaskNotifierTests"
/// </summary>
[TestFixture]
public class TaskNotifierTests
{
    private const string TaskId = "task-17";
    private const string Org = "acme";
    private const string Ds = "site-a";
    private const string User = "jane-id";
    private static readonly DateTime Started = new(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);

    private RegistryContext _db = null!;
    private Mock<IEmailService> _email = null!;
    private Mock<ITaskNotificationLevels> _levels = null!;
    private Mock<IUserRecipients> _recipients = null!;
    private Mock<IUserDatasetAccess> _access = null!;
    private Mock<ITaskNotificationComposer> _composer = null!;
    private Mock<IHeavyToolRegistry> _tools = null!;
    private List<TaskNotificationModel> _models = null!;
    private List<EmailMessage> _queued = null!;
    private BuildNotificationLevel _level;

    [SetUp]
    public void SetUp()
    {
        _db = TestDb.CreateContext<RegistryContext>();
        _level = BuildNotificationLevel.Failed;
        _models = [];
        _queued = [];

        _email = new Mock<IEmailService>();
        _email.SetupGet(e => e.IsEnabled).Returns(true);
        _email.Setup(e => e.QueueAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((message, _) => _queued.Add(message))
            .ReturnsAsync("email-job");
        _levels = new Mock<ITaskNotificationLevels>();
        _levels.Setup(l => l.TasksOfAsync(User, It.IsAny<CancellationToken>())).ReturnsAsync(() => _level);
        _recipients = new Mock<IUserRecipients>();
        _recipients.Setup(r => r.FindAsync(User, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmailAddress("jane@example.com", "Jane"));
        _access = new Mock<IUserDatasetAccess>();
        _access.Setup(a => a.CanReadAsync(User, Org, Ds, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _composer = new Mock<ITaskNotificationComposer>();
        _composer.Setup(c => c.ComposeAsync(It.IsAny<EmailAddress>(), It.IsAny<TaskNotificationModel>()))
            .Callback<EmailAddress, TaskNotificationModel>((_, model) => _models.Add(model))
            .ReturnsAsync((EmailAddress to, TaskNotificationModel model) =>
                new EmailMessage([to], $"{model.Task} {model.Result}", "text", "<p>html</p>"));
        var tool = new Mock<IHeavyTool>();
        tool.SetupGet(t => t.Title).Returns("Photogrammetry (NodeODX)");
        _tools = new Mock<IHeavyToolRegistry>();
        _tools.Setup(t => t.Resolve("photogrammetry", "1")).Returns(tool.Object);

        Add(Job());
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private static JobIndex Job(Action<JobIndex>? change = null)
    {
        var job = new JobIndex
        {
            JobId = TaskId, OrgSlug = Org, DsSlug = Ds, UserId = User, ToolId = "photogrammetry", ToolVersion = "1",
            CurrentState = "Failed", TargetLabel = "flight-1/*.jpg", ErrorMessage = "The processing node ran out of memory",
            CreatedAtUtc = Started.AddMinutes(-5), ProcessingAtUtc = Started,
            FailedAtUtc = Started.AddSeconds(17 * 60 + 4), SucceededAtUtc = Started.AddSeconds(90)
        };
        change?.Invoke(job);
        return job;
    }

    private void Add(JobIndex job)
    {
        _db.JobIndices.Add(job);
        _db.SaveChanges();
    }

    private TaskNotifier Notifier(RegistryContext? db = null) =>
        new(db ?? _db, _email.Object, _levels.Object, _recipients.Object, _access.Object, _composer.Object,
            new BuildNotificationOutbox(db ?? _db, TimeProvider.System), TimeProvider.System,
            NullLogger<TaskNotifier>.Instance, _tools.Object);

    private static FinishedTask Failed(Exception? error = null, string toolId = "photogrammetry") =>
        new(TaskId, toolId, error ?? new InvalidOperationException("boom"));

    private static FinishedTask Succeeded() => new(TaskId, "photogrammetry", null);

    private void Replace(Action<JobIndex> change)
    {
        var row = _db.JobIndices.Single(j => j.JobId == TaskId);
        change(row);
        _db.SaveChanges();
    }

    [Test]
    public async Task AFailedTask_WithFailed_QueuesOneEmail_WithTheLabel_TheStoredError_AndTheDuration()
    {
        (await Notifier().NotifyAsync(Failed(), default)).ShouldBeTrue();

        _queued.ShouldHaveSingleItem().Subject.ShouldBe("Photogrammetry (NodeODX) failed");
        _models.ShouldHaveSingleItem().ShouldBe(new TaskNotificationModel("Jane", "Photogrammetry (NodeODX)",
            "flight-1/*.jpg", Org, Ds, false, "The processing node ran out of memory", TimeSpan.FromSeconds(1024)));
    }

    [Test]
    public async Task ASucceededTask_IsNotToldWithFailed_ButIsWithAll()
    {
        (await Notifier().NotifyAsync(Succeeded(), default)).ShouldBeFalse();
        _queued.ShouldBeEmpty();

        _level = BuildNotificationLevel.All;
        (await Notifier().NotifyAsync(Succeeded(), default)).ShouldBeTrue();

        // The duration of a task that succeeded runs to its own end, and it has no error
        _models.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            m => m.Succeeded.ShouldBeTrue(),
            m => m.Error.ShouldBeNull(),
            m => m.Duration.ShouldBe(TimeSpan.FromSeconds(90)));
    }

    [Test]
    public async Task WithNone_NothingIsSent_AndNothingIsRecorded()
    {
        _level = BuildNotificationLevel.None;

        (await Notifier().NotifyAsync(Failed(), default)).ShouldBeFalse();

        _queued.ShouldBeEmpty();
        _db.BuildNotifications.ShouldBeEmpty();
    }

    [TestCase(HeavyToolIds.Build)]
    public async Task TheBuildTool_IsABuildNotification_NotATaskNotification(string toolId)
    {
        Replace(j => j.ToolId = toolId);

        (await Notifier().NotifyAsync(Failed(toolId: toolId), default)).ShouldBeFalse();

        _queued.ShouldBeEmpty();
    }

    [Test]
    public async Task AChildTask_IsNotReported()
    {
        Replace(j => j.ParentJobId = "parent");

        (await Notifier().NotifyAsync(Failed(), default)).ShouldBeFalse();

        _queued.ShouldBeEmpty();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(MagicStrings.AutoBuildServiceUserId)]
    public async Task ATaskOfTheSystemOrTheBuildService_HasNoOneToTell(string? userId)
    {
        Replace(j => j.UserId = userId);

        (await Notifier().NotifyAsync(Failed(), default)).ShouldBeFalse();

        _queued.ShouldBeEmpty();
    }

    [Test]
    public async Task WithEmailOff_NoQueryIsMade()
    {
        _email.SetupGet(e => e.IsEnabled).Returns(false);

        (await Notifier().NotifyAsync(Failed(), default)).ShouldBeFalse();

        _levels.VerifyNoOtherCalls();
        _db.BuildNotifications.ShouldBeEmpty();
    }

    [Test]
    public async Task AUserWithoutAnAddress_IsNotToldAndNothingIsRecorded()
    {
        _recipients.Setup(r => r.FindAsync(User, It.IsAny<CancellationToken>())).ReturnsAsync((EmailAddress?)null);

        (await Notifier().NotifyAsync(Failed(), default)).ShouldBeFalse();

        _queued.ShouldBeEmpty();
        _db.BuildNotifications.ShouldBeEmpty();
    }

    [Test]
    public async Task AUserWhoCannotReadTheDatasetAnyMore_IsNotTold()
    {
        _access.Setup(a => a.CanReadAsync(User, Org, Ds, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        (await Notifier().NotifyAsync(Failed(), default)).ShouldBeFalse();

        _queued.ShouldBeEmpty();
    }

    [Test]
    public async Task ADatasetThatIsGone_IsNotReported()
    {
        Replace(j => j.DsSlug = SlugKeyedRows.TombstonePrefix + "gone");

        (await Notifier().NotifyAsync(Failed(), default)).ShouldBeFalse();

        _queued.ShouldBeEmpty();
    }

    [Test]
    public async Task ATaskWithoutARow_IsNotReported()
    {
        _db.JobIndices.RemoveRange(_db.JobIndices);
        _db.SaveChanges();

        (await Notifier().NotifyAsync(Failed(), default)).ShouldBeFalse();
    }

    [Test]
    public async Task TheSameEnd_AppliedTwice_QueuesOneEmail_AndLeavesTheSentRecord()
    {
        // The second application is another notifier, as on another host (the unique key of the record decides)
        await Notifier().NotifyAsync(Failed(), default);
        (await Notifier().NotifyAsync(Failed(), default)).ShouldBeFalse();

        _queued.Count.ShouldBe(1);
        var record = _db.BuildNotifications.AsNoTracking().ShouldHaveSingleItem();
        record.ShouldSatisfyAllConditions(
            r => r.UserId.ShouldBe(User),
            r => r.Outcome.ShouldBe("TaskFailed"),
            r => r.RequestJobId.ShouldBe(TaskId),
            r => r.SentAtUtc.ShouldNotBeNull(),
            r => r.PendingKey.ShouldBeNull(),
            r => r.Error.ShouldBe("The processing node ran out of memory"));
    }

    [Test]
    public async Task ARecordedTaskEnd_IsNeverClaimedByTheBuildDigest()
    {
        await Notifier().NotifyAsync(Failed(), default);

        var claimed = await new BuildNotificationOutbox(_db, TimeProvider.System).ClaimAsync(User, "claim", default);

        claimed.ShouldBeEmpty();
    }

    [Test]
    public async Task ATaskRunAgainFromItsFailure_IsToldAgainWhenItEndsAgain()
    {
        var notifier = Notifier();
        await notifier.NotifyAsync(Failed(), default);

        await notifier.RunAgainAsync(TaskId, default);
        (await notifier.NotifyAsync(Failed(), default)).ShouldBeTrue();

        _queued.Count.ShouldBe(2);
        _db.BuildNotifications.Count().ShouldBe(2);
    }

    [Test]
    public async Task AnEmailThatCannotBeQueued_LeavesNoRecord_SoTheEndCanBeToldLater()
    {
        _email.Setup(e => e.QueueAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("job storage unavailable"));

        await Should.ThrowAsync<InvalidOperationException>(() => Notifier().NotifyAsync(Failed(), default));
        _db.BuildNotifications.ShouldBeEmpty();

        _email.Setup(e => e.QueueAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((message, _) => _queued.Add(message))
            .ReturnsAsync("email-job");
        (await Notifier().NotifyAsync(Failed(), default)).ShouldBeTrue();
        _queued.Count.ShouldBe(1);
    }

    [Test]
    public async Task WithoutAStoredError_TheMessageOfTheException_IsScrubbedOfServerPaths()
    {
        Replace(j => j.ErrorMessage = null);

        await Notifier().NotifyAsync(Failed(new InvalidOperationException("cannot read /data/ddb/acme/ds/a.tif")), default);

        var error = _models.ShouldHaveSingleItem().Error.ShouldNotBeNull();
        error.ShouldNotContain("/data/ddb");
    }

    [Test]
    public async Task ALongError_IsCutAtTheLengthTheOutboxKeeps()
    {
        Replace(j => j.ErrorMessage = new string('x', 900));

        await Notifier().NotifyAsync(Failed(), default);

        var error = _models.ShouldHaveSingleItem().Error.ShouldNotBeNull();
        error.Length.ShouldBe(BuildNotification.ErrorMaxLength);
        error.ShouldEndWith("…");
    }

    [Test]
    public async Task ATaskWithoutAStart_HasNoDuration_AndAToolThatIsGone_IsNamedByItsId()
    {
        Replace(j =>
        {
            j.ProcessingAtUtc = null;
            j.ToolId = "retired-tool";
        });

        await Notifier().NotifyAsync(Failed(toolId: "retired-tool"), default);

        _models.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            m => m.Duration.ShouldBeNull(),
            m => m.Task.ShouldBe("retired-tool"));
    }


    private static string CreatedDatasetRecord() =>
        TaskWriteRecord.ForCreatedDataset(new TaskCreatedDataset(Org, Guid.Parse("11111111-2222-3333-4444-555555555555"), Ds)).ToJson();

    [Test]
    public async Task AFailedTask_ThatCreatedItsDataset_SaysTheDatasetIsRemoved_ForItsRollbackDeletesIt()
    {
        Replace(j => j.WriteRecordJson = CreatedDatasetRecord());

        (await Notifier().NotifyAsync(Failed(), default)).ShouldBeTrue();

        _models.ShouldHaveSingleItem().DatasetRemoved.ShouldBeTrue();
    }

    [Test]
    public async Task AFailedTask_OfAnExistingDataset_AndASucceededOneThatCreatedIt_KeepTheDatasetLink()
    {
        // No created dataset in the record (an import into an existing dataset)
        Replace(j => j.WriteRecordJson = new TaskWriteRecord().ToJson());
        await Notifier().NotifyAsync(Failed(), default);

        // The dataset a task created is kept when the task succeeded
        _level = BuildNotificationLevel.All;
        Replace(j => j.WriteRecordJson = CreatedDatasetRecord());
        await Notifier().NotifyAsync(Succeeded(), default);

        _models.Count.ShouldBe(2);
        _models.ShouldAllBe(m => !m.DatasetRemoved);
    }

    [TestCase("NodeODX task/new failed (500): see https://node.internal:3000/task/new?token=s3cr3t-t0ken&x=1 for details", "s3cr3t-t0ken")]
    [TestCase("Request to http://user:hunter2@node.internal/info failed", "hunter2")]
    [TestCase("cannot write /srv/registry/ddb/acme/site-a/.ddb/build/abc/cog.tif", "/srv/registry")]
    public async Task TheErrorTheEmailCarries_HasNoTokenCredentialOrServerPath_WhateverTheStoredMessageWas(string stored, string secret)
    {
        Replace(j => j.ErrorMessage = stored);

        await Notifier().NotifyAsync(Failed(), default);

        var error = _models.ShouldHaveSingleItem().Error.ShouldNotBeNull();
        error.ShouldNotContain(secret);
        error.ShouldNotContain("token=");
    }

    [Test]
    public async Task WithoutAStoredError_TheNodeUrlOfTheException_LosesItsToken()
    {
        Replace(j => j.ErrorMessage = null);

        await Notifier().NotifyAsync(Failed(new InvalidOperationException(
            "NodeODX asset download 'all.zip' failed (404): GET https://node.internal:3000/task/abc/download/all.zip?token=s3cr3t-t0ken")), default);

        var error = _models.ShouldHaveSingleItem().Error.ShouldNotBeNull();
        error.ShouldBe("NodeODX asset download 'all.zip' failed (404): GET https://node.internal:3000/task/abc/download/all.zip");
    }
}
