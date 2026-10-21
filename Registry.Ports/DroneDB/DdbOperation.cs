using System;
using System.Threading;

namespace Registry.Ports.DroneDB;

/// <summary>
/// The operation the current code runs for (a background job, XI-08), as an id that flows with
/// the async context: the libddb adapter hands it to libddb (<c>DDBSetThreadOperation</c>) before
/// each native call, so libddb's log records of that call (and of its worker threads) carry it to
/// <see cref="IDdbLogSink.Write"/>. 0 means none.
/// </summary>
public static class DdbOperation
{
    private static readonly AsyncLocal<ulong> CurrentId = new();

    /// <summary>The id of the current operation, 0 for none.</summary>
    public static ulong Current => CurrentId.Value;

    /// <summary>
    /// Makes <paramref name="id"/> the current operation until the returned scope is disposed, which
    /// restores the previous one. Enter and dispose in the same flow (both synchronous, or both in
    /// one async method).
    /// </summary>
    public static Scope Enter(ulong id)
    {
        var previous = CurrentId.Value;
        CurrentId.Value = id;
        return new Scope(previous);
    }

    /// <summary>Restores the previous operation when disposed.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly ulong _previous;

        internal Scope(ulong previous) => _previous = previous;

        public void Dispose() => CurrentId.Value = _previous;
    }
}
