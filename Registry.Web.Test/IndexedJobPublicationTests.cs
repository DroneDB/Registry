#nullable enable
using System;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Incomplete index publication cannot expose a runnable job or hide an enqueue failure.
/// </summary>
[TestFixture]
public class IndexedJobPublicationTests
{
    [Test]
    public void IndexWriteFailureCancelsUnpublishedJobAndPropagates()
    {
        var storage = new InMemoryStorage();
        using var storageScope = new JobStorageScope(storage);
        var writer = new Mock<IJobIndexWriter>();
        string? stagedId = null;
        writer.Setup(x => x.UpsertOnEnqueueAsync(It.IsAny<string>(), It.IsAny<IndexPayload>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), default))
            .Callback<string, IndexPayload, DateTime, string, System.Threading.CancellationToken>((id, _, _, _, _) =>
            {
                stagedId = id;
                using var connection = storage.GetConnection();
                connection.GetStateData(id).Name.ShouldBe(IndexedJobEnqueuer.PreparationStateName);
            }).ThrowsAsync(new InvalidOperationException("index unavailable"));
        using var provider = new ServiceCollection().AddSingleton(writer.Object).BuildServiceProvider();
        var enqueuer = new IndexedJobEnqueuer(new BackgroundJobClient(storage), provider,
            Mock.Of<ILogger<IndexedJobEnqueuer>>(), storage);
        Should.Throw<InvalidOperationException>(() => enqueuer.Enqueue(() => HangfireUtils.DummyJob(null!),
            new IndexPayload("org", "ds", null, MagicStrings.AutoBuildServiceUserId))).Message.ShouldBe("index unavailable");
        using var connection = storage.GetConnection();
        connection.GetStateData(stagedId).Name.ShouldBe("Deleted");
        writer.Verify(x => x.UpdateStateAsync(stagedId!, "Deleted", It.IsAny<DateTime>(), default), Times.Once);
    }

    [Test]
    public void RejectedDispatchCancelsOnlyThePreparationState()
    {
        var storage = new InMemoryStorage();
        using var storageScope = new JobStorageScope(storage);
        var real = new BackgroundJobClient(storage);
        var client = new Mock<IBackgroundJobClient>();
        string? stagedId = null;
        client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns<Job, IState>((job, state) =>
            stagedId = real.Create(job, state));
        client.Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()))
            .Returns<string, IState, string>((id, state, expected) =>
                state is DeletedState && real.ChangeState(id, state, expected));
        var writer = new Mock<IJobIndexWriter>();
        using var provider = new ServiceCollection().AddSingleton(writer.Object).BuildServiceProvider();
        var enqueuer = new IndexedJobEnqueuer(client.Object, provider, Mock.Of<ILogger<IndexedJobEnqueuer>>(), storage);
        Should.Throw<InvalidOperationException>(() => enqueuer.Enqueue(() => HangfireUtils.DummyJob(null!),
            new IndexPayload("org", "ds", null, MagicStrings.AutoBuildServiceUserId)));
        using var connection = storage.GetConnection();
        connection.GetStateData(stagedId).Name.ShouldBe("Deleted");
        client.Verify(x => x.ChangeState(stagedId!, It.IsAny<DeletedState>(), IndexedJobEnqueuer.PreparationStateName), Times.Once);
    }
}
