#nullable enable
using System;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;

namespace Registry.Web.Services.HeavyTasks;

/// <summary>
/// The disk-space API of the Registry's writers (RQ-10, XI-06): the free space of the volume a path
/// is on (<see cref="VolumeInfo"/>, the path's own mount, RQ-02) and the head-room a write must leave
/// there. The head-room is the caller's safety margin plus libddb's free-disk floor
/// (<c>minFreeDiskMb</c> from <c>DDBGetEffectiveConfig</c>), so an extraction or a download never
/// leaves less than the builds of the files it writes need.
/// </summary>
public sealed class VolumeHeadroom
{
    private const long MiB = 1024L * 1024;

    private readonly Func<string, VolumeInfo?> _measure;
    private readonly ILogger<VolumeHeadroom> _logger;
    private readonly Lazy<long> _floor;

    public VolumeHeadroom(IDdbRuntime runtime, ILogger<VolumeHeadroom> logger)
        : this(runtime, logger, VolumeInfo.For)
    {
    }

    /// <param name="measure">Reads a path's volume (tests replace the system mount table).</param>
    internal VolumeHeadroom(IDdbRuntime runtime, ILogger<VolumeHeadroom> logger, Func<string, VolumeInfo?> measure)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _measure = measure;
        _logger = logger;
        // The process configuration is set before DDBRegisterProcess and never changes afterwards
        _floor = new Lazy<long>(() => DdbEffectiveConfig.Parse(runtime.GetEffectiveConfig()).MinFreeDiskMb * MiB);
    }

    /// <summary>libddb's free-disk floor in bytes (0 when libddb's check is off).</summary>
    public long FloorBytes => _floor.Value;

    /// <summary>
    /// Free bytes on the volume of <paramref name="path"/>, or <see cref="long.MaxValue"/> when they
    /// cannot be read (the guards then rely on their size caps: best effort).
    /// </summary>
    public long AvailableBytes(string path)
    {
        try
        {
            return _measure(path)?.AvailableFreeBytes ?? long.MaxValue;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot read the free disk space of '{Path}'", path);
            return long.MaxValue;
        }
    }

    /// <summary>
    /// The bytes a write must leave free: <paramref name="safetyMarginBytes"/> plus libddb's floor.
    /// A margin of 0 (or less) turns the disk guard off, as configured.
    /// </summary>
    public long MarginBytes(long safetyMarginBytes) => safetyMarginBytes > 0 ? safetyMarginBytes + FloorBytes : 0;

    /// <summary>The streaming guard of a write to <paramref name="destination"/>: the cap, then the head-room.</summary>
    /// <param name="maxBytes">The size cap; 0 (or less) disables it.</param>
    /// <param name="destination">A path on the volume written to.</param>
    /// <param name="safetyMarginBytes">The caller's margin (see <see cref="MarginBytes"/>).</param>
    public ExtractionBudget CreateBudget(long maxBytes, string destination, long safetyMarginBytes) =>
        new(maxBytes, destination, MarginBytes(safetyMarginBytes), AvailableBytes);

    /// <summary>Refuses a write of <paramref name="requiredBytes"/> to <paramref name="path"/> that would eat into the head-room.</summary>
    /// <exception cref="QuotaExceededException">Not enough free space.</exception>
    public void EnsureFree(string path, long requiredBytes, long safetyMarginBytes)
    {
        var available = AvailableBytes(path);
        if (available == long.MaxValue)
            return;

        if (available - requiredBytes < MarginBytes(safetyMarginBytes))
            throw new QuotaExceededException(
                "Not enough free disk space. " +
                $"Required: {CommonUtils.GetBytesReadable(requiredBytes)} plus {CommonUtils.GetBytesReadable(MarginBytes(safetyMarginBytes))} " +
                $"kept free, available: {CommonUtils.GetBytesReadable(available)}.");
    }
}
