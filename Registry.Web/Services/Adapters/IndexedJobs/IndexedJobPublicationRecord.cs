using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hangfire.States;
using Registry.Web.Models;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Destination state an indexed job enters once its publication completes.
/// </summary>
public enum IndexedJobDestination
{
    Immediate,
    Scheduled
}

/// <summary>
/// Outcome of reading a publication record back from <c>Indexing</c> state data.
/// </summary>
public enum IndexedJobRecordParseStatus
{
    Valid,
    Invalid,
    NewerSchema
}

/// <summary>
/// Result of <see cref="IndexedJobPublicationRecord.TryParse"/>: a valid record, or the reason it cannot be used.
/// </summary>
public sealed record IndexedJobRecordParseResult(
    IndexedJobRecordParseStatus Status,
    IndexedJobPublicationRecord? Record,
    string? Reason)
{
    public static IndexedJobRecordParseResult Valid(IndexedJobPublicationRecord record) =>
        new(IndexedJobRecordParseStatus.Valid, record, null);

    public static IndexedJobRecordParseResult Invalid(string reason) =>
        new(IndexedJobRecordParseStatus.Invalid, null, reason);

    public static IndexedJobRecordParseResult NewerSchema(int version) =>
        new(IndexedJobRecordParseStatus.NewerSchema, null,
            $"Publication record schema version {version} is newer than supported version {IndexedJobPublicationRecord.CurrentSchemaVersion}");
}

/// <summary>
/// Versioned execution intent of an indexed job, persisted in its initial <c>Indexing</c> state so
/// that an interrupted publication can be completed later on the same job id.
/// </summary>
public sealed record IndexedJobPublicationRecord
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>State data key holding the schema version (readable without parsing the JSON).</summary>
    public const string SchemaVersionKey = "SchemaVersion";

    /// <summary>State data key holding the JSON publication record.</summary>
    public const string RecordKey = "PublicationRecord";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    public int SchemaVersion { get; }

    /// <summary>Original creation time, truncated to whole milliseconds so database round-trips compare equal.</summary>
    public DateTime CreatedAtUtc { get; }

    public IndexPayload Payload { get; }

    public IndexedJobDestination Destination { get; }

    /// <summary>Resolved <see cref="EnqueuedState"/> queue; set only for <see cref="IndexedJobDestination.Immediate"/>.</summary>
    public string? Queue { get; }

    /// <summary>Absolute due time; set only for <see cref="IndexedJobDestination.Scheduled"/>.</summary>
    public DateTime? EnqueueAtUtc { get; }

    private IndexedJobPublicationRecord(int schemaVersion, DateTime createdAtUtc, IndexPayload payload,
        IndexedJobDestination destination, string? queue, DateTime? enqueueAtUtc)
    {
        SchemaVersion = schemaVersion;
        CreatedAtUtc = createdAtUtc;
        Payload = payload;
        Destination = destination;
        Queue = queue;
        EnqueueAtUtc = enqueueAtUtc;
    }

    /// <summary>Record for a job that is enqueued on <paramref name="queue"/> once published.</summary>
    public static IndexedJobPublicationRecord ForImmediate(IndexPayload payload, DateTime createdAtUtc, string queue) =>
        new(CurrentSchemaVersion, TruncateToMilliseconds(createdAtUtc), Normalize(payload),
            IndexedJobDestination.Immediate, queue, null);

    /// <summary>
    /// Record for a job scheduled <paramref name="delay"/> after its creation. The absolute due time is
    /// computed once here, so a later publication never restarts the delay.
    /// </summary>
    public static IndexedJobPublicationRecord ForScheduled(IndexPayload payload, DateTime createdAtUtc, TimeSpan delay)
    {
        var created = TruncateToMilliseconds(createdAtUtc);
        return new(CurrentSchemaVersion, created, Normalize(payload), IndexedJobDestination.Scheduled, null,
            created.Add(delay));
    }

    /// <summary>Truncates to whole milliseconds and marks the value as UTC.</summary>
    public static DateTime TruncateToMilliseconds(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    /// <summary>Builds the Hangfire state that completes publication.</summary>
    public IState CreateDestinationState() => Destination switch
    {
        IndexedJobDestination.Immediate => new EnqueuedState(Queue!),
        IndexedJobDestination.Scheduled => new ScheduledState(EnqueueAtUtc!.Value),
        _ => throw new InvalidOperationException($"Unsupported publication destination '{Destination}'")
    };

    /// <summary>Returns why the record cannot be published, or null when it is valid.</summary>
    public string? Validate()
    {
        if (SchemaVersion < 1 || SchemaVersion > CurrentSchemaVersion)
            return $"Unsupported schema version {SchemaVersion}";
        if (CreatedAtUtc == default || CreatedAtUtc.Kind != DateTimeKind.Utc)
            return "Creation time is missing or not UTC";
        if (Payload is null)
            return "Payload is missing";
        if (string.IsNullOrWhiteSpace(Payload.OrgSlug) || string.IsNullOrWhiteSpace(Payload.DsSlug))
            return "Payload organization or dataset is missing";
        if (string.IsNullOrWhiteSpace(Payload.ToolId))
            return "Payload tool id is missing";

        switch (Destination)
        {
            case IndexedJobDestination.Immediate:
                if (string.IsNullOrWhiteSpace(Queue)) return "Immediate destination has no queue";
                if (EnqueueAtUtc != null) return "Immediate destination has a scheduled time";
                break;
            case IndexedJobDestination.Scheduled:
                if (EnqueueAtUtc is not { Kind: DateTimeKind.Utc } || EnqueueAtUtc.Value == default)
                    return "Scheduled destination has no UTC scheduled time";
                if (Queue != null) return "Scheduled destination has a queue";
                break;
            default:
                return $"Unsupported destination '{Destination}'";
        }

        try
        {
            // Hangfire validates queue names in the state constructor; a record it would reject
            // can never be published, so it is invalid rather than transiently failing.
            CreateDestinationState();
        }
        catch (Exception ex)
        {
            return $"Destination state cannot be built: {ex.Message}";
        }

        return null;
    }

    /// <summary>Serialized state data for the <c>Indexing</c> state.</summary>
    public Dictionary<string, string> ToStateData() => new()
    {
        [SchemaVersionKey] = SchemaVersion.ToString(CultureInfo.InvariantCulture),
        [RecordKey] = JsonSerializer.Serialize(ToDocument(), JsonOptions)
    };

    /// <summary>
    /// Reads a record from <c>Indexing</c> state data. Never throws: malformed data is reported as
    /// <see cref="IndexedJobRecordParseStatus.Invalid"/>, a record from a newer publisher as
    /// <see cref="IndexedJobRecordParseStatus.NewerSchema"/>.
    /// </summary>
    public static IndexedJobRecordParseResult TryParse(IDictionary<string, string>? stateData)
    {
        try
        {
            if (stateData is null)
                return IndexedJobRecordParseResult.Invalid("State data is missing");

            // Schema version first: a newer publisher may use a shape this version cannot read.
            int? outerVersion = null;
            if (stateData.TryGetValue(SchemaVersionKey, out var outerText))
            {
                if (!int.TryParse(outerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    return IndexedJobRecordParseResult.Invalid($"Schema version '{outerText}' is not a number");
                if (parsed > CurrentSchemaVersion)
                    return IndexedJobRecordParseResult.NewerSchema(parsed);
                outerVersion = parsed;
            }

            if (!stateData.TryGetValue(RecordKey, out var json) || string.IsNullOrWhiteSpace(json))
                return IndexedJobRecordParseResult.Invalid("Publication record is missing");

            int innerVersion;
            using (var document = JsonDocument.Parse(json))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("schemaVersion", out var versionElement) ||
                    !versionElement.TryGetInt32(out innerVersion))
                    return IndexedJobRecordParseResult.Invalid("Publication record has no schema version");
            }

            if (innerVersion > CurrentSchemaVersion)
                return IndexedJobRecordParseResult.NewerSchema(innerVersion);
            if (outerVersion is null)
                return IndexedJobRecordParseResult.Invalid("State data has no schema version");
            if (outerVersion != innerVersion)
                return IndexedJobRecordParseResult.Invalid(
                    $"Schema version mismatch: state data {outerVersion}, record {innerVersion}");

            var doc = JsonSerializer.Deserialize<RecordDocument>(json, JsonOptions);
            if (doc is null)
                return IndexedJobRecordParseResult.Invalid("Publication record is empty");
            if (doc.Payload is null)
                return IndexedJobRecordParseResult.Invalid("Payload is missing");
            IndexedJobDestination destination;
            switch (doc.Destination)
            {
                case nameof(IndexedJobDestination.Immediate): destination = IndexedJobDestination.Immediate; break;
                case nameof(IndexedJobDestination.Scheduled): destination = IndexedJobDestination.Scheduled; break;
                default: return IndexedJobRecordParseResult.Invalid($"Unsupported destination '{doc.Destination}'");
            }

            var p = doc.Payload;
            var record = new IndexedJobPublicationRecord(doc.SchemaVersion, doc.CreatedAtUtc,
                new IndexPayload(p.OrgSlug!, p.DsSlug!, p.Hash, p.UserId, p.Queue, p.Path, p.ToolId!, p.ToolVersion!,
                    p.RequestHash, p.ParentJobId, p.WorkflowExecutionId),
                destination, doc.Queue, doc.EnqueueAtUtc);

            var reason = record.Validate();
            return reason is null
                ? IndexedJobRecordParseResult.Valid(record)
                : IndexedJobRecordParseResult.Invalid(reason);
        }
        catch (Exception ex)
        {
            return IndexedJobRecordParseResult.Invalid($"Publication record cannot be read: {ex.Message}");
        }
    }

    // Blank tool id/version are stored as the JobIndex defaults, so the record carries the
    // identity the index actually uses and always validates.
    private static IndexPayload Normalize(IndexPayload payload) => payload with
    {
        ToolId = string.IsNullOrWhiteSpace(payload.ToolId) ? "build" : payload.ToolId,
        ToolVersion = string.IsNullOrWhiteSpace(payload.ToolVersion) ? "1" : payload.ToolVersion
    };

    private RecordDocument ToDocument() => new()
    {
        SchemaVersion = SchemaVersion,
        CreatedAtUtc = CreatedAtUtc,
        Destination = Destination.ToString(),
        Queue = Queue,
        EnqueueAtUtc = EnqueueAtUtc,
        Payload = new PayloadDocument
        {
            OrgSlug = Payload.OrgSlug,
            DsSlug = Payload.DsSlug,
            Hash = Payload.Hash,
            UserId = Payload.UserId,
            Queue = Payload.Queue,
            Path = Payload.Path,
            ToolId = Payload.ToolId,
            ToolVersion = Payload.ToolVersion,
            RequestHash = Payload.RequestHash,
            ParentJobId = Payload.ParentJobId,
            WorkflowExecutionId = Payload.WorkflowExecutionId
        }
    };

    // Explicit wire format, decoupled from IndexPayload so its evolution cannot silently
    // change stored records. Unknown members are ignored on read.
    private sealed class RecordDocument
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
        [JsonPropertyName("destination")] public string? Destination { get; set; }
        [JsonPropertyName("queue")] public string? Queue { get; set; }
        [JsonPropertyName("enqueueAtUtc")] public DateTime? EnqueueAtUtc { get; set; }
        [JsonPropertyName("payload")] public PayloadDocument? Payload { get; set; }
    }

    private sealed class PayloadDocument
    {
        [JsonPropertyName("orgSlug")] public string? OrgSlug { get; set; }
        [JsonPropertyName("dsSlug")] public string? DsSlug { get; set; }
        [JsonPropertyName("hash")] public string? Hash { get; set; }
        [JsonPropertyName("userId")] public string? UserId { get; set; }
        [JsonPropertyName("queue")] public string? Queue { get; set; }
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("toolId")] public string? ToolId { get; set; }
        [JsonPropertyName("toolVersion")] public string? ToolVersion { get; set; }
        [JsonPropertyName("requestHash")] public string? RequestHash { get; set; }
        [JsonPropertyName("parentJobId")] public string? ParentJobId { get; set; }
        [JsonPropertyName("workflowExecutionId")] public string? WorkflowExecutionId { get; set; }
    }
}
