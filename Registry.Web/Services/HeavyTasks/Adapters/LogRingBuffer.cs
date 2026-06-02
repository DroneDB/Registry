#nullable enable
using System;
using System.Collections.Generic;
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
    [JsonPropertyName("lines")] public List<LogLine> Lines { get; set; } = new();
    [JsonPropertyName("cursor")] public long Cursor { get; set; }
    [JsonPropertyName("truncatedFromTail")] public long TruncatedFromTail { get; set; }
}

/// <summary>
/// Bounded ring buffer of recent log lines (spec §6.2). Keeps at most
/// <c>maxLines</c> lines OR <c>maxBytes</c> of message payload - whichever limit
/// is hit first evicts from the head. Maintains a monotonic <c>Cursor</c> used as
/// the <c>?since=N</c> long-poll token and counts lines lost to truncation.
/// Not thread-safe; callers must serialize access (the progress sink does).
/// </summary>
public sealed class LogRingBuffer
{
    private readonly int _maxLines;
    private readonly int _maxBytes;
    private readonly LinkedList<LogLine> _lines = new();
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
        Lines = new List<LogLine>(_lines),
        Cursor = _cursor,
        TruncatedFromTail = _truncatedFromTail
    };

    /// <summary>Serializes the current snapshot to compact JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(Snapshot());
}
