using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Services.Objects;

/// <summary>
/// Mutual exclusion by key, in this process: one holder per key at a time, several keys taken together in one fixed
/// order (so two callers that need the same keys in a different order never wait on each other). A key nobody holds or
/// waits for is forgotten, so the table holds only the keys in use.
/// </summary>
internal sealed class KeyedAsyncLock
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    /// <summary>Waits for every key, taking them in ordinal order.</summary>
    /// <returns>The lease; disposing it releases the keys.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> ended the wait; nothing is held.</exception>
    public async Task<IDisposable> AcquireAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        var lease = new Lease(this);
        try
        {
            foreach (var key in keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var slot = Rent(key);
                try
                {
                    await slot.Semaphore.WaitAsync(ct);
                }
                catch
                {
                    Return(key, slot);
                    throw;
                }

                lease.Add(key, slot);
            }
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        return lease;
    }

    private Slot Rent(string key)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out var slot))
                _slots[key] = slot = new Slot();
            slot.Users++;
            return slot;
        }
    }

    private void Return(string key, Slot slot)
    {
        lock (_gate)
        {
            if (--slot.Users != 0)
                return;

            _slots.Remove(key);
            slot.Dispose();
        }
    }

    private sealed class Slot : IDisposable
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;

        public void Dispose() => Semaphore.Dispose();
    }

    private sealed class Lease(KeyedAsyncLock owner) : IDisposable
    {
        private readonly List<(string Key, Slot Slot)> _held = [];

        public void Add(string key, Slot slot) => _held.Add((key, slot));

        public void Dispose()
        {
            // Release in the reverse order of the acquisition
            for (var i = _held.Count - 1; i >= 0; i--)
            {
                var (key, slot) = _held[i];
                slot.Semaphore.Release();
                owner.Return(key, slot);
            }

            _held.Clear();
        }
    }
}
