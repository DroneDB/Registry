#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Per-user upload reservations against one storage evaluation per window (singleton state behind
/// <see cref="UploadQuotaGuard"/>). Within a window the used size is
/// <c>evaluated use + stored since + in flight</c>; at the next evaluation the stored uploads are
/// part of the evaluated use and only the reservations still in flight carry over.
/// </summary>
public sealed class UploadQuotaLedger
{
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

    private sealed class Reservation(UploadQuotaLedger ledger, UserState state, long bytes) : IQuotaReservation
    {
        private bool _done;
        public long Bytes { get; private set; } = bytes;

        public void Resize(long bytes)
        {
            lock (state)
            {
                if (_done) throw new InvalidOperationException("The reservation is already released");
                var delta = bytes - Bytes;
                if (delta > 0) ledger.EnsureFits(state, delta);
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

    public UploadQuotaLedger(IOptions<AppSettings> settings)
        : this(TimeSpan.FromSeconds((settings.Value.Uploads ?? new UploadSettings()).QuotaWindowSeconds))
    {
    }

    public UploadQuotaLedger(TimeSpan window)
    {
        _window = window;
    }

    /// <summary>
    /// Reserves <paramref name="bytes"/> for <paramref name="userId"/>; <paramref name="evaluate"/>
    /// (the user's storage use and limit, null when unlimited) runs at most once per window.
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
                return new Reservation(this, state, bytes);
            }
        }
    }

    private void EnsureFits(UserState state, long bytes)
    {
        if (state.Unlimited) return;
        var usage = state.Used + state.StoredSinceEvaluation + state.InFlight + bytes;
        if (usage > state.Total)
            throw new QuotaExceededException(usage, state.Total);
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

/// <summary>Default <see cref="IUploadQuotaGuard"/>: the current user's limit from <see cref="IUtils"/>.</summary>
public sealed class UploadQuotaGuard(UploadQuotaLedger ledger, IUtils utils) : IUploadQuotaGuard
{
    public Task<IQuotaReservation> ReserveAsync(string userId, long bytes) =>
        ledger.ReserveAsync(userId, bytes, async () => await utils.GetCurrentUserStorageLimit());
}
