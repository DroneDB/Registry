#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// A single log line stored in the ring buffer. <c>t</c> is a Unix-ms timestamp.
/// </summary>
public sealed class LogLine
{
    [JsonPropertyName("t")] public long T { get; set; }
    [JsonPropertyName("lvl")] public string Lvl { get; set; } = "info";
    [JsonPropertyName("msg")] public string Msg { get; set; } = "";
    [JsonPropertyName("phase")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Phase { get; set; }
}

/// <summary>
/// Serializable snapshot of the ring buffer, persisted to <c>JobIndex.LogTailJson</c>
/// and returned by the long-poll log endpoint.
/// </summary>
public sealed class LogTailSnapshot
{
    [JsonPropertyName("lines")] public List<LogLine> Lines { get; set; } = [];
    [JsonPropertyName("cursor")] public long Cursor { get; set; }
    [JsonPropertyName("truncatedFromTail")] public long TruncatedFromTail { get; set; }

    /// <summary>
    /// Lenient counterpart of <see cref="LogRingBuffer.ToJson"/>: a null, empty or
    /// malformed payload yields an empty snapshot instead of throwing, so a corrupted
    /// <c>JobIndex.LogTailJson</c> can never break a status or log response. An explicit
    /// null <c>lines</c> and entries that are null or carry an out-of-range timestamp
    /// (both would throw later in <see cref="LogTailSnapshotExtensions.AsStrings"/>)
    /// are normalized away before the snapshot is returned.
    /// </summary>
    public static LogTailSnapshot Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new LogTailSnapshot();
        try
        {
            var snap = JsonSerializer.Deserialize<LogTailSnapshot>(json) ?? new LogTailSnapshot();
            // An explicit JSON null replaces the non-null initializer; range-check T so
            // FromUnixTimeMilliseconds can never throw on AsStrings.
            if (snap.Lines is null)
            {
                snap.Lines = [];
            }
            else
            {
                snap.Lines = [.. snap.Lines.Where(l => l is not null && l.T >= MinUnixMs && l.T <= MaxUnixMs)];
            }
            return snap;
        }
        catch
        {
            return new LogTailSnapshot();
        }
    }

    private static readonly long MinUnixMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    private static readonly long MaxUnixMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();
}

/// <summary>
/// Rendering helpers for <see cref="LogTailSnapshot"/>.
/// </summary>
public static class LogTailSnapshotExtensions
{
    /// <summary>Formats the buffered lines as <c>[HH:mm:ss] message</c>, resolving the Unix-ms timestamps.</summary>
    public static IReadOnlyList<string> AsStrings(this LogTailSnapshot snap) =>
        [.. snap.Lines.Select(l => $"[{DateTimeOffset.FromUnixTimeMilliseconds(l.T):HH:mm:ss}] {l.Msg}")];
}

/// <summary>
/// Bounded ring buffer of recent log lines. Keeps at most
/// <c>maxLines</c> lines OR <c>maxBytes</c> of message payload - whichever limit
/// is hit first evicts from the head. Maintains a monotonic <c>Cursor</c> used as
/// the <c>?since=N</c> long-poll token and counts lines lost to truncation.
/// Not thread-safe; callers must serialize access (the progress sink does).
/// </summary>
public sealed class LogRingBuffer
{
    private readonly int _maxLines;
    private readonly int _maxBytes;
    private readonly LinkedList<LogLine> _lines = [];
    private long _byteCount;
    private long _cursor;
    private long _truncatedFromTail;

    public LogRingBuffer(int maxLines = 200, int maxBytes = 32768)
    {
        _maxLines = maxLines < 1 ? 1 : maxLines;
        _maxBytes = maxBytes < 1 ? 1 : maxBytes;
    }

    /// <summary>Monotonic cursor; equals the total number of lines ever appended.</summary>
    public long Cursor => _cursor;

    /// <summary>Number of lines evicted from the head due to capacity limits.</summary>
    public long TruncatedFromTail => _truncatedFromTail;

    public int Count => _lines.Count;

    /// <summary>Appends a line, evicting from the head to honor the limits.</summary>
    public void Append(string message, string level = "info", string? phase = null, long? unixMs = null)
    {
        var line = new LogLine
        {
            T = unixMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Lvl = level,
            Msg = message ?? string.Empty,
            Phase = phase
        };

        var size = Encoding.UTF8.GetByteCount(line.Msg);
        _lines.AddLast(line);
        _byteCount += size;
        _cursor++;

        // Evict from the head while over either limit (keep at least one line).
        while (_lines.Count > 1 && (_lines.Count > _maxLines || _byteCount > _maxBytes))
        {
            var head = _lines.First!.Value;
            _byteCount -= Encoding.UTF8.GetByteCount(head.Msg);
            _lines.RemoveFirst();
            _truncatedFromTail++;
        }
    }

    /// <summary>Produces a serializable snapshot of the current buffer state.</summary>
    public LogTailSnapshot Snapshot() => new()
    {
        Lines = [.._lines],
        Cursor = _cursor,
        TruncatedFromTail = _truncatedFromTail
    };

    /// <summary>Serializes the current snapshot to compact JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(Snapshot());
}
