#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// Bridges a managed progress observer and a <see cref="CancellationToken"/> to
/// <c>DDBProgressCallback</c> (<c>int (*)(double fraction, const char *phase, void *userData)</c>;
/// a non-zero return asks the call to cancel). XI-03: the callback is an
/// <c>[UnmanagedCallersOnly]</c> static method that finds this instance through a
/// <see cref="GCHandle"/> passed as <c>userData</c>, so no delegate has to be kept alive and nothing
/// managed can unwind through the native frames (the runtime would end the process): an exception
/// thrown by the observer is captured, the call is asked to cancel, and
/// <see cref="ThrowIfFaulted"/> rethrows it (original type and stack) once the call has returned.
/// </summary>
/// <remarks>
/// Usage: <c>using var callback = NativeProgressCallback.Create(...)</c>, pass
/// <see cref="FunctionPointer"/> and <see cref="UserData"/> to the call (both null/zero without a
/// callback), then call <see cref="ThrowIfFaulted"/> before mapping the result: the observer's
/// exception takes precedence over the canceled result it caused.
/// </remarks>
internal sealed unsafe class NativeProgressCallback : IDisposable
{
    private const int Continue = 0;
    private const int Cancel = 1;

    private readonly Action<double, string?>? _progress;
    private readonly CancellationToken _cancellationToken;
    private GCHandle _self;
    private ExceptionDispatchInfo? _fault;

    private NativeProgressCallback(Action<double, string?>? progress, CancellationToken cancellationToken)
    {
        _progress = progress;
        _cancellationToken = cancellationToken;
        _self = GCHandle.Alloc(this);
    }

    /// <summary>The callback to hand to native code.</summary>
    public static delegate* unmanaged[Cdecl]<double, byte*, IntPtr, int> FunctionPointer => &OnProgress;

    /// <summary>The <c>userData</c> to hand to native code with <see cref="FunctionPointer"/>.</summary>
    public IntPtr UserData => GCHandle.ToIntPtr(_self);

    /// <summary>
    /// A bridge, or <c>null</c> when there is nothing to observe (no observer and a token that
    /// cannot be canceled), so the native call runs without a callback.
    /// </summary>
    public static NativeProgressCallback? Create(Action<double, string?>? progress,
        CancellationToken cancellationToken) =>
        progress == null && !cancellationToken.CanBeCanceled
            ? null
            : new NativeProgressCallback(progress, cancellationToken);

    /// <summary>Rethrows the first exception thrown by the observer, if any.</summary>
    public void ThrowIfFaulted() => Volatile.Read(ref _fault)?.Throw();

    public void Dispose()
    {
        if (_self.IsAllocated)
            _self.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnProgress(double fraction, byte* phase, IntPtr userData)
    {
        try
        {
            return GCHandle.FromIntPtr(userData).Target is NativeProgressCallback callback
                ? callback.Report(fraction, phase)
                : Cancel;
        }
        catch
        {
            // Nothing may leave an UnmanagedCallersOnly method; Report captures the observer's own faults
            return Cancel;
        }
    }

    private int Report(double fraction, byte* phase)
    {
        if (Volatile.Read(ref _fault) != null || _cancellationToken.IsCancellationRequested)
            return Cancel;

        if (_progress == null)
            return Continue;

        try
        {
            _progress(fraction, phase == null ? null : Marshal.PtrToStringUTF8((IntPtr)phase));
            return Continue;
        }
        catch (Exception ex)
        {
            // Native code may report from several threads: keep the first fault
            Interlocked.CompareExchange(ref _fault, ExceptionDispatchInfo.Capture(ex), null);
            return Cancel;
        }
    }
}
