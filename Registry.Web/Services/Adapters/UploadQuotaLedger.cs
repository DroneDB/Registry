using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Per-owner upload reservations (the owner of the target organization, FR-XC-26) against one storage evaluation per window (singleton state behind
/// <see cref="UploadQuotaGuard"/>). Within a window the used size is
/// <c>evaluated use + stored since + in flight</c>; at the next evaluation the stored uploads are
/// part of the evaluated use and only the reservations still in flight carry over.
/// </summary>
public sealed class UploadQuotaLedger
{
    [SuppressMessage("Design", "CA1001", Justification =
        "Evaluation is only awaited and released, never asked for its AvailableWaitHandle, so it holds nothing " +
        "to dispose; disposing it when the state is dropped could race with a reservation still waiting on it.")]
    private sealed class UserState
    {
        public readonly SemaphoreSlim Evaluation = new(1, 1);
        public long EvaluatedAtTicks = long.MinValue;
        public bool Unlimited;
        public long Used;
        public long? Total;
        public long StoredSinceEvaluation;
        public long InFlight;
    }

    private sealed class Reservation(UserState state, long bytes) : IQuotaReservation
    {
        private bool _done;
        public long Bytes { get; private set; } = bytes;

        public void Resize(long bytes)
        {
            lock (state)
            {
                if (_done) throw new InvalidOperationException("The reservation is already released");
                var delta = bytes - Bytes;
                if (delta > 0) EnsureFits(state, delta);
                state.InFlight += delta;
                Bytes = bytes;
            }
        }

        public void Commit()
        {
            lock (state)
            {
                if (_done) return;
                _done = true;
                state.InFlight -= Bytes;
                state.StoredSinceEvaluation += Bytes;
            }
        }

        public IQuotaReservation Split(long bytes)
        {
            lock (state)
            {
                if (_done) throw new InvalidOperationException("The reservation is already released");
                ArgumentOutOfRangeException.ThrowIfNegative(bytes);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, Bytes);
                // In flight already: the bytes change hands, the total of the owner stays
                Bytes -= bytes;
                return new Reservation(state, bytes);
            }
        }

        public void Dispose()
        {
            lock (state)
            {
                if (_done) return;
                _done = true;
                state.InFlight -= Bytes;
            }
        }
    }

    private const int SweepThreshold = 256;

    private readonly ConcurrentDictionary<string, UserState> _users = new();
    private readonly TimeSpan _window;

    public UploadQuotaLedger(IOptions<UploadSettings> settings)
        : this(TimeSpan.FromSeconds(settings.Value.QuotaWindowSeconds))
    {
    }

    public UploadQuotaLedger(TimeSpan window)
    {
        _window = window;
    }

    /// <summary>
    /// Reserves <paramref name="bytes"/> for <paramref name="userId"/> (the owner whose quota applies);
    /// <paramref name="evaluate"/> (the owner's storage use and limit, null when unlimited) runs at most once per window.
    /// </summary>
    public async Task<IQuotaReservation> ReserveAsync(string userId, long bytes, Func<Task<UserStorageInfo?>> evaluate)
    {
        if (_users.Count > SweepThreshold) Sweep();

        while (true)
        {
            var state = _users.GetOrAdd(userId, _ => new UserState());

            if (Expired(state))
            {
                await state.Evaluation.WaitAsync();
                try
                {
                    if (Expired(state))
                    {
                        var info = await evaluate();
                        lock (state)
                        {
                            state.Unlimited = info?.Total == null;
                            state.Used = info?.Used ?? 0;
                            state.Total = info?.Total;
                            state.StoredSinceEvaluation = 0;
                            Volatile.Write(ref state.EvaluatedAtTicks, Environment.TickCount64);
                        }
                    }
                }
                finally
                {
                    state.Evaluation.Release();
                }
            }

            lock (state)
            {
                // A sweep may have dropped this (idle) state meanwhile: reserve on the live one
                if (!_users.TryGetValue(userId, out var live) || !ReferenceEquals(live, state))
                    continue;

                EnsureFits(state, bytes);
                state.InFlight += bytes;
                return new Reservation(state, bytes);
            }
        }
    }

    /// <summary>
    /// Whether the evaluation of the window says that a limit applies to <paramref name="userId"/>: false when none was
    /// made yet or when it found none (no owner, limiter off, administrator). Read from the state that
    /// <see cref="ReserveAsync"/> keeps, so a caller that has just reserved asks no second evaluation.
    /// </summary>
    public bool LimitApplies(string userId)
    {
        if (!_users.TryGetValue(userId, out var state))
            return false;

        lock (state)
            return state.EvaluatedAtTicks != long.MinValue && !state.Unlimited;
    }

    private static void EnsureFits(UserState state, long bytes)
    {
        if (state.Unlimited) return;
        var usage = state.Used + state.StoredSinceEvaluation + state.InFlight + bytes;
        if (usage > state.Total)
            throw QuotaExceededException.OwnerOverQuota(state.Total.GetValueOrDefault() - (usage - bytes));
    }

    private bool Expired(UserState state)
    {
        var evaluatedAt = Volatile.Read(ref state.EvaluatedAtTicks);
        return evaluatedAt == long.MinValue ||
               Environment.TickCount64 - evaluatedAt >= (long)_window.TotalMilliseconds;
    }

    // Forget users with nothing in flight whose evaluation expired
    private void Sweep()
    {
        foreach (var (userId, state) in _users.ToArray())
        {
            lock (state)
            {
                if (state.InFlight == 0 && Expired(state))
                    _users.TryRemove(new(userId, state));
            }
        }
    }
}

/// <summary>
/// Storage-quota admission for uploads (C.6). The storage use of the owner of the target organization (FR-XC-26,
/// D-039: whoever uploads, the quota is the owner's) is evaluated once per window and every upload reserves its size
/// against it, so a burst of uploads costs one evaluation instead of one per file, and an upload over quota is refused
/// before its first byte.
/// </summary>
public sealed class UploadQuotaGuard(UploadQuotaLedger ledger, IStorageQuota quota, ChunkStore store,
    RegistryContext context)
{
    /// <summary>
    /// Reserves <paramref name="bytes"/> against the quota of the owner <paramref name="ownerId"/> of the organization
    /// that an upload writes into (empty: no owner, no limit).
    /// </summary>
    /// <exception cref="Registry.Web.Exceptions.QuotaExceededException">The upload does not fit.</exception>
    public Task<IQuotaReservation> ReserveAsync(string ownerId, long bytes) =>
        ledger.ReserveAsync(ownerId, bytes, async () => await quota.GetOwnerStorageLimitAsync(ownerId));

    /// <summary>
    /// Reserves <paramref name="bytes"/> and counts the open chunked uploads of the owner against them: the room that
    /// the open uploads hold (see <see cref="OpenUploadBytesAsync"/>) is part of the reservation until the caller
    /// resizes or releases it, so a file that does not fit beside them is refused before its first byte. The reservation
    /// is taken before the count: an upload that reserves after this one sees it in flight, and one that reserves
    /// before it is on disk when the count runs; both may count it for that moment, which refuses near the limit but
    /// never lets two files through over it.
    /// </summary>
    /// <param name="ddb">The dataset written into (the organization of an upload that counts is one of the owner's).</param>
    /// <param name="ownerId">The owner of the organization whose quota applies.</param>
    /// <param name="bytes">The size of the file or files to store.</param>
    /// <param name="userId">The user of the upload that is checked, whose session is not counted (with <paramref name="fileId"/>).</param>
    /// <param name="fileId">The file id of the chunked upload that is checked; null for a plain upload.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="Registry.Web.Exceptions.QuotaExceededException">The upload does not fit with the open uploads.</exception>
    public async Task<IQuotaReservation> ReserveBesideOpenUploadsAsync(IDDB ddb, string ownerId, long bytes,
        string? userId, string? fileId, CancellationToken ct = default)
    {
        var reservation = await ReserveAsync(ownerId, bytes);
        try
        {
            reservation.Resize(bytes + await OpenUploadBytesAsync(ddb, ownerId, userId, fileId, ct));
            return reservation;
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The size of the files of the open chunked uploads of every user in the organizations of the owner
    /// <paramref name="ownerId"/> (every dataset folder of each of them; the organization of <paramref name="ddb"/> is one):
    /// the storage that they hold, in the quota of the owner, which the upload ledger reserves per owner, until they are
    /// committed or removed, which the storage use of the owner does not count yet. The session of
    /// <paramref name="userId"/> and <paramref name="fileId"/> in <paramref name="ddb"/> is not counted: it is the upload
    /// being checked (two first chunks of one file sent at once: the second must not count the session the first one
    /// just started); without a file id nothing is left out. Without an owner no limit applies: 0.
    /// </summary>
    private async Task<long> OpenUploadBytesAsync(IDDB ddb, string ownerId, string? userId, string? fileId,
        CancellationToken ct)
    {
        // No limit (no owner, limiter off, administrator owner): nothing to count, and no folder scan on every upload. The
        // reservation just made carries the evaluation of the window: no second evaluation per upload of a burst
        if (string.IsNullOrEmpty(ownerId) || !ledger.LimitApplies(ownerId))
            return 0;

        // The folder of a dataset is <datasets>/<organization slug>/<dataset> (DdbManager.GetDatasetFolder)
        var dataset = Path.GetFullPath(ddb.DatasetFolderPath);
        var organization = Path.GetDirectoryName(dataset);
        var datasets = organization == null ? null : Path.GetDirectoryName(organization);
        if (organization == null || datasets == null)
            return 0;

        var owned = await context.Organizations.AsNoTracking()
            .Where(org => org.OwnerId == ownerId)
            .Select(org => org.Slug)
            .ToListAsync(ct);
        var organizations = owned.Select(slug => Path.GetFullPath(Path.Combine(datasets, slug)))
            .Append(organization)
            .Distinct(PathComparison.Comparer)
            .Where(Directory.Exists);

        // In one dataset the session of a user and a file id is one folder (SessionKey): its manifest has both
        bool Current(string folder, ChunkStatus s) =>
            fileId != null && s.Manifest.FileId == fileId && s.Manifest.UserId == userId &&
            Path.GetFullPath(folder) == dataset;

        return organizations
            .SelectMany(Directory.EnumerateDirectories)
            .SelectMany(folder => store.List(Path.Combine(folder, IDDB.UploadsFolderName, DatasetUploads.ChunksFolderName))
                .Where(s => !Current(folder, s)))
            .Sum(s => s.Manifest.TotalSize);
    }
}
