using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Services.Builds;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// The records of a heavy task that <see cref="HeavyTaskJobWrapper"/> keeps while it runs, from the run's own scope: its
/// job index row, its Hangfire job, and its write record with the one rollback of what it wrote.
/// </summary>
/// <param name="Index">The task's job index row: its error, progress and artifact.</param>
/// <param name="Jobs">The task's Hangfire job: its state, its delete when it is cancelled, the TTL delete of its work folder.</param>
/// <param name="Rollback">The attempt rule and the rollback of what the task wrote.</param>
/// <param name="WriteRecords">The task's write record (its commit).</param>
/// <param name="EntryChanges">The announce of the entries the task wrote or removed.</param>
public sealed record HeavyTaskRecords(
    IJobIndexWriter Index,
    IBackgroundJobsProcessor Jobs,
    ITaskRollback Rollback,
    ITaskWriteRecordStore WriteRecords,
    TaskEntryChanges EntryChanges);

/// <summary>
/// What a heavy task needs of its dataset to run, from the run's own scope (<see cref="HeavyTaskJobWrapper"/>): the
/// database, the dataset's index, the builds it queues and the storage quota it writes under.
/// </summary>
/// <param name="Db">The database: the dataset and the task's row.</param>
/// <param name="DdbManager">The index of the dataset.</param>
/// <param name="Builds">The build jobs of the entries the task adds.</param>
/// <param name="Scheduler">The build scheduler the task queues its builds through.</param>
/// <param name="Storage">The storage quota of the dataset's organization.</param>
public sealed record HeavyTaskDatasetServices(
    RegistryContext Db,
    IDdbManager DdbManager,
    DdbJobs Builds,
    BuildScheduler Scheduler,
    IStorageQuota Storage);
