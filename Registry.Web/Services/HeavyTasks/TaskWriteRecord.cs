using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks;

/// <summary>Where the write record of a task is (FR-XC-27, D-045).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TaskWriteState>))]
public enum TaskWriteState
{
    /// <summary>The task may write, be cancelled and be rolled back.</summary>
    [JsonStringEnumMemberName("open")] Open,

    /// <summary>A user asked to cancel the task: it can no longer commit.</summary>
    [JsonStringEnumMemberName("cancelRequested")] CancelRequested,

    /// <summary>The task published its output (or deletes its inputs): it is never cancelled nor rolled back.</summary>
    [JsonStringEnumMemberName("committed")] Committed,

    /// <summary>A rollback claimed the record and is deleting what it holds (or stopped half-way: it is resumed).</summary>
    [JsonStringEnumMemberName("rollingBack")] RollingBack,

    /// <summary>The rollback finished.</summary>
    [JsonStringEnumMemberName("rolledBack")] RolledBack
}

/// <summary>
/// A path or folder a running task recorded and has not written to its record yet (<c>TaskWriteRecorder</c>), or the
/// stamp of a path it finished writing (<paramref name="Stamp"/>: <see cref="TaskWriteRecord.StampOf"/>).
/// </summary>
public readonly record struct PendingWrite(DatasetKey Dataset, string Path, bool IsFolder, string? Stamp = null);

/// <summary>
/// The sources a committed task deletes after its commit (FR-XC-27): kept in its record, so that a task lost after its
/// commit names those that remain even when its request is gone with the job storage (InMemory storage, a restart).
/// </summary>
/// <param name="Org">The organization slug of the dataset.</param>
/// <param name="InternalRef">The dataset's folder id.</param>
/// <param name="Paths">The sources, dataset-relative.</param>
public sealed record TaskCommittedSources(
    [property: JsonPropertyName("org")] string Org,
    [property: JsonPropertyName("internalRef")] Guid InternalRef,
    [property: JsonPropertyName("paths")] IReadOnlyList<string> Paths);

/// <summary>The dataset a task created (an import into a new dataset, a photogrammetry run with a new dataset).</summary>
/// <param name="Org">The organization slug.</param>
/// <param name="InternalRef">The dataset's folder id: the dataset is found by it, never by its slug, which may change.</param>
/// <param name="Slug">The slug at creation, for the logs.</param>
public sealed record TaskCreatedDataset(
    [property: JsonPropertyName("org")] string Org,
    [property: JsonPropertyName("internalRef")] Guid InternalRef,
    [property: JsonPropertyName("slug")] string Slug);

/// <summary>The paths and folders a task created in one dataset (dataset-relative, '/'-separated).</summary>
public sealed class TaskWrittenDataset
{
    [JsonPropertyName("org")] public string Org { get; set; } = "";
    [JsonPropertyName("internalRef")] public Guid InternalRef { get; set; }
    [JsonPropertyName("paths")] public List<string> Paths { get; set; } = [];
    [JsonPropertyName("folders")] public List<string> Folders { get; set; } = [];

    /// <summary>
    /// The size and time of each path once the task finished writing it (<see cref="TaskWriteRecord.StampOf"/>): a file
    /// whose stamp changed since was replaced by somebody else, and a rollback keeps it.
    /// </summary>
    [JsonPropertyName("stamps")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Stamps { get; set; }

    /// <summary>The dataset as a background job names it.</summary>
    [JsonIgnore] public DatasetKey Key => new(Org, InternalRef);
}

/// <summary>
/// The write record of a task (<c>JobIndex.WriteRecordJson</c>, FR-XC-27, D-045): the dataset the task created and the
/// paths and folders it created, each recorded before it was written and only when it did not exist. A rollback deletes
/// only these; a file the task replaced is never in it. Every change of the stored record is a compare-and-swap on the
/// whole JSON string (<see cref="ITaskWriteRecordStore"/>).
/// </summary>
public sealed class TaskWriteRecord
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Written first, so that a reader of the state alone (<see cref="StateOf"/>) stops at once.</summary>
    [JsonPropertyName("state")] public TaskWriteState State { get; set; } = TaskWriteState.Open;

    /// <summary>The Hangfire retry count of the attempt that last started (the attempt rule of the worker).</summary>
    [JsonPropertyName("attempt")] public int Attempt { get; set; }

    [JsonPropertyName("createdDataset")] public TaskCreatedDataset? CreatedDataset { get; set; }

    [JsonPropertyName("datasets")] public List<TaskWrittenDataset> Datasets { get; set; } = [];

    /// <summary>
    /// The job of the delayed delete of <see cref="CreatedDataset"/> (<see cref="CreatedDatasetRollbackJob"/>) that the
    /// rollback scheduled, until that job decides (deletes the dataset, or keeps it): a startup sweep schedules it again
    /// when the job storage lost it (InMemory storage, a restart).
    /// </summary>
    [JsonPropertyName("createdDatasetDeleteJob")] public string? CreatedDatasetDeleteJob { get; set; }

    /// <summary>
    /// The claim of the rollback that holds the record (a new id at each claim): a claim always changes the stored
    /// record, so two rollbacks never both win the compare-and-swap of one claim.
    /// </summary>
    [JsonPropertyName("claim")] public string? Claim { get; set; }

    /// <summary>
    /// A cancel came while a rollback held the record (the cleanup of a lost run before the task runs again): the record
    /// that cleanup writes back is <see cref="TaskWriteState.CancelRequested"/>, so the task can no longer commit.
    /// </summary>
    [JsonPropertyName("cancelPending")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CancelPending { get; set; }

    /// <summary>The sources the task deletes after its commit, written with the commit.</summary>
    [JsonPropertyName("committedSources")] public TaskCommittedSources? CommittedSources { get; set; }

    /// <summary>Whether the task recorded any path or folder.</summary>
    [JsonIgnore] public bool HasWrites => Datasets.Any(d => d.Paths.Count > 0 || d.Folders.Count > 0);

    /// <summary>Whether a rollback may claim the record (it is not committed and not rolled back).</summary>
    [JsonIgnore]
    public bool NeedsRollback => State is TaskWriteState.Open or TaskWriteState.CancelRequested or TaskWriteState.RollingBack;

    /// <summary>The record a task submitted with a dataset created for it starts with.</summary>
    public static TaskWriteRecord ForCreatedDataset(TaskCreatedDataset created) => new() { CreatedDataset = created };

    /// <summary>
    /// The record a task re-queued by the user (Retry) starts with (§8.2): empty, with the dataset created for the task,
    /// so that a rollback of the new run still deletes it and its delayed delete sees the task run again. Null keeps
    /// <paramref name="record"/>: a task without a record stays without one, a committed record is never reset (such a
    /// task is not retried, D-037), and a rollback that has not finished keeps what it could not delete yet, which the
    /// attempt rule of the new run deletes before it writes.
    /// </summary>
    public static TaskWriteRecord? ForRequeue(TaskWriteRecord? record) =>
        record is null || record.State is TaskWriteState.RollingBack or TaskWriteState.Committed
            ? null
            : record.CreatedDataset is { } created
                ? ForCreatedDataset(created)
                : new TaskWriteRecord();

    /// <summary>The entry of <paramref name="dataset"/>, added when missing.</summary>
    public TaskWrittenDataset For(DatasetKey dataset)
    {
        var found = Datasets.FirstOrDefault(d => d.InternalRef == dataset.InternalRef &&
                                                 string.Equals(d.Org, dataset.OrgSlug, StringComparison.Ordinal));
        if (found != null)
            return found;

        found = new TaskWrittenDataset { Org = dataset.OrgSlug, InternalRef = dataset.InternalRef };
        Datasets.Add(found);
        return found;
    }

    /// <summary>Adds the paths and folders not recorded yet (compared as written: the tools record their own spelling).</summary>
    public void Add(DatasetKey dataset, IEnumerable<string> paths, IEnumerable<string> folders,
        IEnumerable<KeyValuePair<string, string>>? stamps = null)
    {
        var entry = For(dataset);
        AddDistinct(entry.Paths, paths);
        AddDistinct(entry.Folders, folders);
        foreach (var (path, stamp) in stamps ?? [])
        {
            if (!entry.Paths.Contains(path))
                entry.Paths.Add(path);
            (entry.Stamps ??= new Dictionary<string, string>(StringComparer.Ordinal))[path] = stamp;
        }
    }

    /// <summary>
    /// The stamp of the file at <paramref name="localPath"/>: its size and last write time (UTC ticks); null when it
    /// is not there.
    /// </summary>
    public static string? StampOf(string localPath)
    {
        var info = new System.IO.FileInfo(localPath);
        return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : null;
    }

    private static void AddDistinct(List<string> target, IEnumerable<string> values)
    {
        var known = new HashSet<string>(target, StringComparer.Ordinal);
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value) && known.Add(value))
                target.Add(value);
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// The record stored as <paramref name="json"/>: null when there is none. A document that cannot be read (written
    /// by no release, or damaged) is an empty open record, so that its next change replaces it.
    /// </summary>
    public static TaskWriteRecord? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var record = JsonSerializer.Deserialize<TaskWriteRecord>(json, JsonOptions) ?? new TaskWriteRecord();
            record.Datasets ??= [];
            record.Datasets.RemoveAll(d => d is null);
            foreach (var dataset in record.Datasets)
            {
                dataset.Paths ??= [];
                dataset.Folders ??= [];
                dataset.Org ??= "";
            }

            return record;
        }
        catch (JsonException)
        {
            return new TaskWriteRecord();
        }
    }

    /// <summary>
    /// The state of the record stored as <paramref name="json"/> (null when there is none) without reading its paths:
    /// the state is the first member this class writes. Used by the task lists, which read many records.
    /// </summary>
    public static TaskWriteState? StateOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        // The state of a record this class wrote is in its first bytes
        const int headLength = 128;
        if (json.Length > headLength && StateIn(Encoding.UTF8.GetBytes(json[..headLength]), false) is { } early)
            return early;

        return StateIn(Encoding.UTF8.GetBytes(json), true) ?? TaskWriteState.Open;
    }

    // The top-level "state" of the document; null when it is not in the bytes read (or an unreadable record: Parse
    // reads it as an empty open one)
    private static TaskWriteState? StateIn(byte[] utf8, bool isFinalBlock)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8, isFinalBlock, default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isState = reader.ValueTextEquals("state"u8);
                if (!reader.Read())
                    return null;
                if (isState)
                    return reader.TokenType == JsonTokenType.String ? StateNamed(reader.GetString()) : TaskWriteState.Open;
                if (!reader.TrySkip())
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static TaskWriteState StateNamed(string? name) => name switch
    {
        "cancelRequested" => TaskWriteState.CancelRequested,
        "committed" => TaskWriteState.Committed,
        "rollingBack" => TaskWriteState.RollingBack,
        "rolledBack" => TaskWriteState.RolledBack,
        _ => TaskWriteState.Open
    };

    /// <summary>Whether the record stored as <paramref name="json"/> is committed (FR-XC-27).</summary>
    public static bool IsCommitted(string? json) => StateOf(json) == TaskWriteState.Committed;
}
