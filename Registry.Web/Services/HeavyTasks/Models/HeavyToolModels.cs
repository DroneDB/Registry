using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.Server;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Builds;

namespace Registry.Web.Services.HeavyTasks.Models;

/// <summary>
/// Access level a tool requires on the target dataset.
/// </summary>
public enum HeavyToolPermission
{
    Read,
    Write
}

/// <summary>A request to execute a heavy tool against a dataset.</summary>
public sealed record HeavyToolRequest(
    string ToolId,
    string Version,
    string OrgSlug,
    string DsSlug,
    string? Path,
    JsonElement Params);

/// <summary>
/// Cheap (O(ms)) pre-submit plan: estimated output size, quota bucket and the
/// default artifact descriptor. Used for quota checks and the pre-submit estimate.
/// </summary>
public sealed record HeavyToolPlan(
    long? EstimatedOutputBytes,
    string QuotaKey,
    string? DefaultFileName,
    string? ContentType);

/// <summary>Descriptor of the artifact produced by a tool execution.</summary>
public sealed record HeavyToolArtifact(
    string RelativePath,
    string ContentType,
    string FileName,
    long SizeBytes,
    string? Sha256 = null);

/// <summary>Incremental progress emitted by a tool during execution.</summary>
public sealed record HeavyToolProgress(
    double Fraction,            // 0..1, or -1 for indeterminate
    string? Phase = null,
    string? Message = null,
    string? LogChunk = null);   // additional text to append to the log tail

/// <summary>
/// Context available to a tool during validation and planning.
/// </summary>
public interface IHeavyToolValidationContext
{
    IDDB Ddb { get; }
    ClaimsPrincipal? Caller { get; }

    /// <summary>
    /// The output file an earlier attempt of this task already wrote (<see cref="PublishedOutputMarker"/>), or null
    /// (at submit, and on the first attempt). A tool that refuses an existing output does not refuse this one.
    /// </summary>
    string? PublishedOutput { get; }

    /// <summary>
    /// Whether the task was queued before tools refused an existing output without <c>overwrite</c> (its record
    /// declares no output paths): a request of then without the <c>overwrite</c> field replaced its output, and still
    /// does when it runs. Always false at submit.
    /// </summary>
    bool QueuedBeforeOverwriteRule { get; }
}

/// <summary>
/// Context available to a tool during execution.
/// </summary>
public interface IHeavyToolExecutionContext : IHeavyToolValidationContext
{
    string TaskId { get; }
    string? WorkDir { get; }    // null when ProducesArtifact = false
    PerformContext Hangfire { get; }

    /// <summary>The task's dataset, as a background job names it.</summary>
    DatasetKey Dataset { get; }

    /// <summary>
    /// The id of the user who requested the task (its record, <c>JobIndex.UserId</c>; a Retry keeps it), or null for a
    /// task of the system. The worker has no <see cref="IHeavyToolValidationContext.Caller"/>: a tool that checks the
    /// requester's access again before it acts on it (photogrammetry creating a dataset, D-031) checks this user.
    /// </summary>
    string? RequestedBy { get; }

    /// <summary>Queues the builds of what the tool added (as children of the task), in the task's scope.</summary>
    BuildScheduler BuildQueue { get; }

    /// <summary>Runs builds inline, in the task's own job (the build and rescan tools).</summary>
    DdbJobs Builds { get; }

    /// <summary>
    /// Records that the task wrote its output file <paramref name="path"/> (<see cref="PublishedOutputMarker"/>):
    /// a later attempt skips the write and indexes it. Called right after the write returns.
    /// </summary>
    void MarkOutputPublished(string path);

    /// <summary>
    /// The write record of the task (FR-XC-27, D-045), for a tool with <see cref="Ports.IHeavyTool.AddsDatasetFiles"/>:
    /// a cancelled, failed or lost task deletes what it recorded, and only that. A tool without it gets a record that
    /// refuses every write.
    /// </summary>
    ITaskWriteRecorder WriteRecord { get; }
}

/// <summary>
/// The write record of a running task (FR-XC-27, D-045). The contract of a tool: it records a path BEFORE it writes it,
/// and only a path that does not exist yet, which it then creates with <c>FileMode.CreateNew</c> (so a file that appeared
/// meanwhile is never taken over); a file it replaces (<c>overwrite</c>, FR-XC-07) is never recorded. The same for a
/// folder it creates. Paths are dataset-relative ('/'-separated). Records are buffered and written in batches
/// (<see cref="FlushIfDueAsync"/>), before <see cref="MarkCommittedAsync"/> and at the end of the task; a failed or
/// cancelled task also deletes the records not written yet.
/// </summary>
public interface ITaskWriteRecorder
{
    /// <summary>Whether the tool may record (it declares <see cref="Ports.IHeavyTool.AddsDatasetFiles"/>).</summary>
    bool Enabled { get; }

    /// <summary>The file <paramref name="path"/> of the task's dataset, about to be created.</summary>
    void Record(string path);

    /// <summary>The file <paramref name="path"/> of <paramref name="dataset"/> (another dataset than the task's), about to be created.</summary>
    void Record(DatasetKey dataset, string path);

    /// <summary>The folder <paramref name="path"/> of the task's dataset, about to be created (deleted by a rollback when empty).</summary>
    void RecordFolder(string path);

    /// <summary>The folder <paramref name="path"/> of <paramref name="dataset"/>, about to be created.</summary>
    void RecordFolder(DatasetKey dataset, string path);

    /// <summary>
    /// The recorded file <paramref name="path"/> of <paramref name="dataset"/> is complete at
    /// <paramref name="localPath"/>: its size and time are recorded, so a rollback keeps it once somebody else
    /// replaced it (an upload with overwrite).
    /// </summary>
    void Written(DatasetKey dataset, string path, string localPath);

    /// <summary>Writes the buffered records when the batch is full or old enough; call it between files.</summary>
    /// <exception cref="Registry.Web.Services.Ports.TaskWriteRecordClosedException">A rollback claimed the record.</exception>
    ValueTask FlushIfDueAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes the buffered records (and stamps). A path is in the stored record before the first byte of it is written
    /// (§8.1): call it after recording a batch of paths and folders and before creating them, and before any step that
    /// deletes or replaces a file.
    /// </summary>
    /// <exception cref="Registry.Web.Services.Ports.TaskWriteRecordClosedException">A rollback claimed the record.</exception>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>
    /// Marks the record committed (the task published its output and is about to delete its inputs, FR-PRC-02): from
    /// now on it is never cancelled nor rolled back.
    /// </summary>
    /// <exception cref="Registry.Web.Services.Ports.TaskWriteRecordClosedException">
    /// A cancel was requested or a rollback claimed the record: the task must stop without deleting anything.
    /// </exception>
    Task MarkCommittedAsync(CancellationToken ct = default);

    /// <summary>Records the dataset the task created in the worker (photogrammetry), right after it was created.</summary>
    Task CreatedDatasetAsync(TaskCreatedDataset dataset, CancellationToken ct = default);

    /// <summary>
    /// The dataset the task recorded as created (<see cref="CreatedDatasetAsync"/>, by this run or an earlier one of the
    /// same task: a Retry and the re-run of a lost run keep it), or null. A run that finds one reuses it instead of
    /// creating a second one (the attempt rule deleted what the earlier run wrote into it).
    /// </summary>
    Task<TaskCreatedDataset?> RecordedCreatedDatasetAsync(CancellationToken ct = default);

    /// <summary>
    /// Drops <paramref name="path"/> of <paramref name="dataset"/> from the record: the tool recorded it but could not
    /// create it (<c>FileMode.CreateNew</c> found a file another writer created meanwhile), so a rollback must not
    /// delete it.
    /// </summary>
    Task ForgetAsync(DatasetKey dataset, string path, CancellationToken ct = default);

    /// <summary>
    /// The paths of <paramref name="dataset"/> the task recorded so far (earlier attempts included): a file at one of
    /// them is the task's own, which a retry may write again.
    /// </summary>
    Task<IReadOnlySet<string>> RecordedPathsAsync(DatasetKey dataset, CancellationToken ct = default);

    /// <summary>
    /// Tells the dataset that entries left it (deleted by the task, e.g. the sources Merge deletes): the same
    /// notification as a rollback (<see cref="TaskEntryChanges"/>).
    /// </summary>
    Task EntriesLeftAsync(DatasetKey dataset, IReadOnlyCollection<string> paths);
}
