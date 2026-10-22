#nullable enable
using System;
using System.Threading;
using Registry.Ports.DroneDB;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// The libddb cancel token of one native call (XI-07, RQ-13): for an export that
/// <c>DDBGetCapabilities().cancellable</c> lists, when the current <see cref="DdbCancellation"/> can be
/// canceled, a native token is created, canceled by the managed token (<c>DDBCancel</c>, callable from any
/// thread), set on the calling thread right before the call (<c>DDBSetThreadCancelToken</c>: for that call
/// only), and freed after it. Every other call runs without one. Used only by <see cref="NativeCall"/>, the
/// one call envelope, so no call site handles a token itself.
/// </summary>
internal readonly struct NativeCancellation : IDisposable
{
    private readonly IntPtr _token;
    private readonly CancellationTokenRegistration _registration;

    private NativeCancellation(IntPtr token, CancellationTokenRegistration registration, CancellationToken managed)
    {
        _token = token;
        _registration = registration;
        Token = managed;
    }

    /// <summary>The managed token the call works for (none when the call has no native token).</summary>
    public CancellationToken Token { get; }

    /// <summary>
    /// Creates the native token of the next call of <paramref name="operation"/> (the export's name), or
    /// nothing. Call it before anything else of the call: <c>DDBCreateCancelToken</c> is an operation call,
    /// which resets the thread's operation id and token when it returns (<see cref="Arm"/> sets the token
    /// after the operation id).
    /// </summary>
    public static NativeCancellation Create(string operation)
    {
        var managed = DdbCancellation.Current;
        if (!managed.CanBeCanceled || !DdbAbi.Verify().IsCancellable(operation))
            return default;

        NativeCall.Check(NativeDdb.DDBCreateCancelToken(out var token), nameof(NativeDdb.DDBCreateCancelToken));

        // Runs at once when the managed token is canceled already: the call then stops at its first checkpoint
        var registration = managed.UnsafeRegister(static state => NativeDdb.DDBCancel((IntPtr)state!), token);
        return new NativeCancellation(token, registration, managed);
    }

    /// <summary>Sets the token on this thread for the next call; right before it (a setting: it keeps the operation id).</summary>
    public void Arm()
    {
        if (_token != IntPtr.Zero)
            NativeDdb.DDBSetThreadCancelToken(_token);
    }

    public void Dispose()
    {
        if (_token == IntPtr.Zero)
            return;

        // Waits for a DDBCancel running on another thread: the token is never canceled after it is freed.
        // DDBFreeCancelToken never changes the last error, so the call's error is still readable.
        _registration.Dispose();
        NativeDdb.DDBFreeCancelToken(_token);
    }
}
