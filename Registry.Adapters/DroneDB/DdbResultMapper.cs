using System;
using System.Runtime.InteropServices;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// Stateless helpers for the native DroneDB C API error surface: reading the native
/// last-error string and mapping final <see cref="DdbResult"/> codes to typed managed
/// exceptions. Public static so it can be unit-tested without <c>InternalsVisibleTo</c>.
/// </summary>
public static class DdbResultMapper
{
    [DllImport("ddb", EntryPoint = "DDBGetLastError")]
    private static extern IntPtr _GetLastError();

    /// <summary>The last error message set by the native library; <c>null</c> if no native call set one.</summary>
    public static string? GetNativeLastError()
    {
        var ptr = _GetLastError();
        return Marshal.PtrToStringUTF8(ptr);
    }

    /// <summary>Native last-error text, with a fallback when the native library set none.</summary>
    public static string SafeGetLastError(string? operation = null)
    {
        return GetNativeLastError() ?? (operation != null ? "Unknown error in " + operation : "Unknown error");
    }

    /// <summary>
    /// Maps a native call's final <see cref="DdbResult"/> to typed exceptions (or returns for
    /// <see cref="DdbResult.Success"/>). Central point where transient contention surfaces as
    /// <see cref="DdbBusyException"/> so Hangfire retry policies (OnlyOn = DdbBusyException) and
    /// the 503 + Retry-After client path actually trigger (ImproveParallelWrites plan, workstream 03).
    /// </summary>
    public static void ThrowForFinalResult(DdbResult result, string operation)
    {
        switch (result)
        {
            case DdbResult.Success:
                return;
            case DdbResult.Busy:
                throw new DdbBusyException(SafeGetLastError(operation));
            case DdbResult.BuildInProgress:
                throw new DdbBuildInProgressException(SafeGetLastError(operation));
            case DdbResult.Canceled:
                throw new DdbCanceledException(SafeGetLastError(operation));
            default:
                throw new DdbException(SafeGetLastError(operation));
        }
    }
}
