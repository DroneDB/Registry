#nullable enable
using System;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Moq;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test.Support;

/// <summary>
/// A background job client that records the cleanup job a dataset delete queues, to run it later on a
/// <see cref="DatasetCleanupService"/> as the Hangfire worker does (what happens between the delete and the cleanup is
/// what the tests of the deleted datasets' job rows are about).
/// </summary>
public sealed class CleanupJobRecorder
{
    public CleanupJobRecorder()
    {
        Jobs.Setup(j => j.Enqueue(It.IsAny<Expression<Func<DatasetCleanupService, Task>>>()))
            .Callback((Expression<Func<DatasetCleanupService, Task>> call) => Cleanup = call)
            .Returns("cleanup-job");
    }

    /// <summary>The client to give the datasets API; <c>Delete(jobId)</c> is what cancels a job.</summary>
    public Mock<IBackgroundJobsProcessor> Jobs { get; } = new();

    /// <summary>The cleanup job that was queued, or null.</summary>
    public Expression<Func<DatasetCleanupService, Task>>? Cleanup { get; private set; }

    /// <summary>Runs the queued cleanup job on <paramref name="service"/>.</summary>
    public Task RunAsync(DatasetCleanupService service) =>
        (Cleanup ?? throw new InvalidOperationException("No cleanup job was queued")).Compile()(service);
}
