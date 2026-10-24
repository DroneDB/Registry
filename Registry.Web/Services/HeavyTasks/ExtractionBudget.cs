#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Registry.Common;
using Registry.Web.Exceptions;

namespace Registry.Web.Services.HeavyTasks;

/// <summary>
/// Shared streaming guard against decompression bombs and disk exhaustion, used by every tool that
/// writes archive/import entries to disk. The deterministic uncompressed-byte <b>cap</b> is the
/// primary limit; the free-disk-space check is a best-effort secondary net that is re-sampled
/// periodically so it never relies on a single stale snapshot. Created by
/// <see cref="VolumeHeadroom.CreateBudget"/>, which measures the volume and adds libddb's floor to
/// the margin.
/// </summary>
public sealed class ExtractionBudget
{
    // Re-sample free disk space at most once per this many bytes written: bounds the number of
    // syscalls while still reacting to space consumed by other processes during a long extraction.
    private const long ResampleEveryBytes = 256L * 1024 * 1024;
    private const int CopyBufferSize = 1024 * 1024;

    private readonly long _maxBytes;          // 0 => no cap
    private readonly string _destinationRoot;
    private readonly long _safetyMarginBytes; // 0 => disk guard disabled
    private readonly Func<string, long> _availableBytes;

    private long _freeDiskBytesAtSample;
    private long _bytesSinceSample;

    /// <summary>Total bytes accounted (written) through this budget so far.</summary>
    public long BytesWritten { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExtractionBudget"/> class.
    /// </summary>
    /// <param name="maxBytes">Absolute uncompressed-size cap in bytes; <c>0</c> (or less) disables the cap.</param>
    /// <param name="destinationRoot">A path on the target volume, used to sample free disk space.</param>
    /// <param name="safetyMarginBytes">Disk head-room to keep free; <c>0</c> (or less) disables the disk guard.</param>
    /// <param name="availableBytes">Free bytes on the volume of a path (<see cref="long.MaxValue"/> when unknown).</param>
    public ExtractionBudget(long maxBytes, string destinationRoot, long safetyMarginBytes,
        Func<string, long> availableBytes)
    {
        _maxBytes = maxBytes > 0 ? maxBytes : 0;
        _destinationRoot = destinationRoot;
        _safetyMarginBytes = safetyMarginBytes > 0 ? safetyMarginBytes : 0;
        _availableBytes = availableBytes ?? throw new ArgumentNullException(nameof(availableBytes));
        _freeDiskBytesAtSample = _availableBytes(destinationRoot);
    }

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="destination"/> in fixed-size chunks,
    /// enforcing the cap (primary) and the disk margin (secondary) BEFORE each write so a single
    /// under-reported or oversized entry cannot exceed the limit mid-copy.
    /// </summary>
    /// <param name="source">The entry stream to read from.</param>
    /// <param name="destination">The file stream to write to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of bytes written for this entry.</returns>
    /// <exception cref="QuotaExceededException">The cap or the disk-space margin would be breached.</exception>
    public async Task<long> CopyGuardedAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[CopyBufferSize];
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            Account(read);
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
        }

        return written;
    }

    /// <summary>
    /// Accounts for <paramref name="additionalBytes"/> about to be written, throwing
    /// <see cref="QuotaExceededException"/> if the cap or the disk-space margin would be breached.
    /// </summary>
    /// <param name="additionalBytes">The number of bytes about to be written.</param>
    /// <exception cref="QuotaExceededException">The cap or the disk-space margin would be breached.</exception>
    public void Account(long additionalBytes)
    {
        var projected = BytesWritten + additionalBytes;

        // Primary, deterministic guard: the uncompressed-size cap.
        if (_maxBytes > 0 && projected > _maxBytes)
            throw new QuotaExceededException(
                "The archive is too large to extract: it exceeds the maximum allowed uncompressed size " +
                $"({CommonUtils.GetBytesReadable(_maxBytes)}).");

        // Secondary, best-effort guard: free disk space. Re-sampled every ResampleEveryBytes so a long
        // extraction reacts to space consumed by other processes instead of trusting a stale snapshot.
        if (_safetyMarginBytes > 0)
        {
            if (_bytesSinceSample >= ResampleEveryBytes)
            {
                _freeDiskBytesAtSample = _availableBytes(_destinationRoot);
                _bytesSinceSample = 0;
            }

            // Free space already reflects bytes written before the last sample, so only subtract the
            // bytes written SINCE the sample plus the pending chunk.
            var estimatedFree = _freeDiskBytesAtSample - _bytesSinceSample - additionalBytes;
            if (estimatedFree < _safetyMarginBytes)
                throw new QuotaExceededException(
                    "Not enough free disk space to extract the archive. " +
                    $"Available: {CommonUtils.GetBytesReadable(_freeDiskBytesAtSample - _bytesSinceSample)}.");

            _bytesSinceSample += additionalBytes;
        }

        BytesWritten = projected;
    }
}
