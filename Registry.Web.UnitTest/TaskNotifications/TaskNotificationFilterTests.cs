using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Filters;
using Registry.Web.Hosting;
using Registry.Web.Models.Email;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Services.TaskNotifications;
using Registry.Web.Services.Users;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.TaskNotifications;

/// <summary>
/// The state filter that tells a user their heavy task ended (FR-PRC-40): the final states of a task other than the build,
/// the failed task that runs again, and a filter that never fails the state change.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~TaskNotificationFilterTests"
/// </summary>
[TestFixture]
public class TaskNotificationFilterTests
{
    private const string TaskId = "task-9";
    private const string User = "jane-id";

    private RegistryContext _db = null!;
    private ServiceProvider _provider = null!;
    private List<EmailMessage> _queued = null!;
    private TaskNotificationFilter _filter = null!;

    [SetUp]
    public void SetUp()
    {
        _db = TestDb.CreateContext<RegistryContext>();
        _db.JobIndices.Add(new JobIndex
        {
            JobId = TaskId, OrgSlug = "acme", DsSlug = "site-a", UserId = User, ToolId = "photogrammetry",
            CurrentState = "Failed", ErrorMessage = "Out of memory", CreatedAtUtc = DateTime.UtcNow
        });
        _db.SaveChanges();

        _queued = [];
        var email = new Mock<IEmailService>();
        email.SetupGet(e => e.IsEnabled).Returns(true);
        email.Setup(e => e.QueueAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((message, _) => _queued.Add(message))
            .ReturnsAsync("email-job");
        var levels = new Mock<ITaskNotificationLevels>();
        levels.Setup(l => l.TasksOfAsync(User, It.IsAny<CancellationToken>())).ReturnsAsync(BuildNotificationLevel.Failed);
        var recipients = new Mock<IUserRecipients>();
        recipients.Setup(r => r.FindAsync(User, It.IsAny<CancellationToken>())).ReturnsAsync(new EmailAddress("jane@example.com", "Jane"));
        var access = new Mock<IUserDatasetAccess>();
        access.Setup(a => a.CanReadAsync(User, "acme", "site-a", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var composer = new Mock<ITaskNotificationComposer>();
        composer.Setup(c => c.ComposeAsync(It.IsAny<EmailAddress>(), It.IsAny<TaskNotificationModel>()))
            .ReturnsAsync((EmailAddress to, TaskNotificationModel model) => new EmailMessage([to], model.Task, "t", "<p>h</p>"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_db);
        services.AddSingleton(email.Object);
        services.AddSingleton(levels.Object);
        services.AddSingleton(recipients.Object);
        services.AddSingleton(access.Object);
        services.AddSingleton(composer.Object);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<BuildNotificationOutbox>();
        services.AddScoped<TaskNotifier>();
        _provider = services.BuildServiceProvider();
        _filter = new TaskNotificationFilter(_provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TaskNotificationFilter>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
        _db.Dispose();
    }

    private static Job TaskJob(string toolId = "photogrammetry") =>
        Job.FromExpression<HeavyTaskJobWrapper>(w => w.Run(toolId, "1", "{}", null!, CancellationToken.None));

    private void Apply(Job job, IState state, string oldState = "Processing")
    {
        var context = new ApplyStateContext(new InMemoryStorage(), Mock.Of<JobStorageConnection>(), Mock.Of<IWriteOnlyTransaction>(),
            new BackgroundJob(TaskId, job, DateTime.UtcNow), state, oldState);

        _filter.OnStateApplied(context, Mock.Of<IWriteOnlyTransaction>());
    }

    private static FailedState Failed() => new(new InvalidOperationException("boom"));

    [Test]
    public void AFailedTask_QueuesOneEmail()
    {
        Apply(TaskJob(), Failed());

        _queued.ShouldHaveSingleItem().Subject.ShouldBe("photogrammetry");
    }

    [Test]
    public void ASucceededTask_IsNotToldToAUserWhoWantsOnlyFailures()
    {
        Apply(TaskJob(), new SucceededState(null, 0, 0));

        _queued.ShouldBeEmpty();
    }

    [Test]
    public void TheBuildTool_IsLeftToTheBuildNotifications()
    {
        Apply(TaskJob(HeavyToolIds.Build), Failed());

        _queued.ShouldBeEmpty();
    }

    [Test]
    public void AnotherJob_IsNotATask()
    {
        Apply(Job.FromExpression<DdbJobs>(j => j.Build("acme", Guid.Empty, "cloud.laz", false, null)), Failed());

        _queued.ShouldBeEmpty();
    }

    [TestCase("Enqueued")]
    [TestCase("Scheduled")]
    public void AStateThatIsNotTheEnd_IsIgnored(string state)
    {
        // Hangfire's own retry of an attempt is Scheduled, never Failed: it is not the end of the task
        Apply(TaskJob(), state == "Enqueued" ? new EnqueuedState("tasks") : new ScheduledState(TimeSpan.FromMinutes(1)));

        _queued.ShouldBeEmpty();
    }

    [Test]
    public void AFailedTaskRunAgain_IsToldAgain_WhenItFailsAgain()
    {
        Apply(TaskJob(), Failed());
        Apply(TaskJob(), new EnqueuedState("tasks"), oldState: FailedState.StateName);
        Apply(TaskJob(), Failed());

        _queued.Count.ShouldBe(2);
    }

    [Test]
    public void ATaskThatFailsAgainWithoutBeingRunAgain_IsToldOnce()
    {
        Apply(TaskJob(), Failed());
        Apply(TaskJob(), Failed());

        _queued.Count.ShouldBe(1);
    }

    [Test]
    public void ANotificationFailure_NeverFailsTheStateChange()
    {
        _db.Dispose();

        Should.NotThrow(() => Apply(TaskJob(), Failed()));
    }

    [Test]
    public void TheFilter_IsInstalled_AfterTheJobIndexStateIsWritten()
    {
        // The error, the files and the times are read from the job index: its row must hold the final state
        var order = RegistryJobsServiceCollectionExtensions.JobFilterTypes.ToList();

        order.IndexOf(typeof(TaskNotificationFilter)).ShouldBeGreaterThan(order.IndexOf(typeof(JobIndexStateFilter)));
    }
}
