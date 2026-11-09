using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

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
public sealed class UploadQuotaGuard(UploadQuotaLedger ledger, IStorageQuota quota)
{
    /// <summary>
    /// Reserves <paramref name="bytes"/> against the quota of the owner <paramref name="ownerId"/> of the organization
    /// that an upload writes into (empty: no owner, no limit).
    /// </summary>
    /// <exception cref="Registry.Web.Exceptions.QuotaExceededException">The upload does not fit.</exception>
    public Task<IQuotaReservation> ReserveAsync(string ownerId, long bytes) =>
        ledger.ReserveAsync(ownerId, bytes, async () => await quota.GetOwnerStorageLimitAsync(ownerId));

    /// <summary>
    /// Whether a storage limit applies to the owner <paramref name="ownerId"/> (false: the limiter is off, the owner is
    /// an administrator or has no limit, or there is no owner), so that callers can skip work that only a limit needs.
    /// </summary>
    public async Task<bool> LimitAppliesAsync(string? ownerId, CancellationToken ct = default) =>
        !string.IsNullOrEmpty(ownerId) && await quota.GetOwnerStorageLimitAsync(ownerId, ct) != null;
}
