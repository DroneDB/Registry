#nullable enable
using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// Native progress callback contract mirroring <c>DDBProgressCallback</c> in ddb.h:
/// <c>int (*)(double fraction, const char *phase, void *userData)</c>.
/// Returning a non-zero value requests cooperative cancellation.
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int DdbProgressCallbackNative(double fraction, IntPtr phase, IntPtr userData);

/// <summary>
/// Bridges a managed progress observer and a <see cref="CancellationToken"/> to
/// <see cref="DdbProgressCallbackNative"/>. A managed exception must never unwind through the
/// native frames (the runtime ends the process), so an exception thrown by the observer is
/// captured, the native call is asked to cancel, and <see cref="ThrowIfFaulted"/> rethrows it
/// (original type and stack) once the P/Invoke has returned.
/// </summary>
/// <remarks>
/// Usage: pass <see cref="Native"/> to the P/Invoke, keep this instance alive across the call
/// (<see cref="GC.KeepAlive"/>), then call <see cref="ThrowIfFaulted"/> before mapping the result:
/// the observer's exception takes precedence over the canceled result it caused.
/// </remarks>
internal sealed class NativeProgressCallback
{
    private const int Continue = 0;
    private const int Cancel = 1;

    private readonly Action<double, string?>? _progress;
    private readonly CancellationToken _cancellationToken;
    private ExceptionDispatchInfo? _fault;

    private NativeProgressCallback(Action<double, string?>? progress, CancellationToken cancellationToken)
    {
        _progress = progress;
        _cancellationToken = cancellationToken;
        Native = Invoke;
    }

    /// <summary>The delegate handed to native code (held by this instance).</summary>
    public DdbProgressCallbackNative Native { get; }

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

    private int Invoke(double fraction, IntPtr phasePtr, IntPtr userData)
    {
        if (Volatile.Read(ref _fault) != null || _cancellationToken.IsCancellationRequested)
            return Cancel;

        if (_progress == null)
            return Continue;

        try
        {
            var phase = phasePtr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(phasePtr);
            _progress(fraction, phase);
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
