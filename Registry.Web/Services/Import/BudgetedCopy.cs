using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks;

namespace Registry.Web.Services.Import;

/// <summary>
/// The one copy loop of every write that the budget bounds (an archive entry, a file downloaded from a URL, a file of a
/// remote registry): each chunk is accounted in the <see cref="ExtractionBudget"/> (the size cap, then the disk
/// head-room) before it is written, so a source that sends more than it said stops at the cap instead of filling the
/// volume.
/// </summary>
public static class BudgetedCopy
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="destination"/> in chunks, accounting each chunk in
    /// <paramref name="budget"/> before it is written.
    /// </summary>
    /// <param name="source">What to read.</param>
    /// <param name="destination">Where to write.</param>
    /// <param name="budget">The cap and the disk head-room of the write.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="read">Reads the next chunk into the buffer (0 at the end); null: a plain read of <paramref name="source"/>.
    /// A caller that guards the reads (a stall timeout) wraps them here.</param>
    /// <param name="chunkWritten">Called after each chunk with the bytes copied so far; it may throw to stop the copy (a
    /// speed guard) or report progress.</param>
    /// <returns>The number of bytes copied.</returns>
    /// <exception cref="QuotaExceededException">The cap or the disk head-room would be breached by the next chunk.</exception>
    public static async Task<long> CopyAsync(Stream source, Stream destination, ExtractionBudget budget,
        CancellationToken ct, Func<Memory<byte>, CancellationToken, ValueTask<int>>? read = null,
        Action<long>? chunkWritten = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(budget);
        read ??= (buffer, token) => source.ReadAsync(buffer, token);

        var chunk = new byte[BufferSize];
        long copied = 0;
        int count;
        while ((count = await read(chunk, ct)) > 0)
        {
            budget.Account(count);
            await destination.WriteAsync(chunk.AsMemory(0, count), ct);
            copied += count;
            chunkWritten?.Invoke(copied);
        }

        return copied;
    }
}
