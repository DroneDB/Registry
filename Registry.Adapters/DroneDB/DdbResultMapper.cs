using System;
using System.Runtime.InteropServices;
using System.Threading;
using Registry.Ports.DroneDB;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// The libddb error contract: every non-success <see cref="DdbResult"/> maps to one typed
/// exception, with the message and reason libddb set for that call. Public static so the mapping is
/// unit-tested without native code (<see cref="Map"/>).
/// <list type="table">
/// <item><term>Busy</term><description><see cref="DdbBusyException"/> (transient contention: retried)</description></item>
/// <item><term>BuildInProgress</term><description><see cref="DdbBuildInProgressException"/> (retried)</description></item>
/// <item><term>Canceled</term><description><see cref="DdbCanceledException"/>, an <see cref="OperationCanceledException"/></description></item>
/// <item><term>NotFound</term><description><see cref="DdbNotFoundException"/> (404) where the call has no "absent" answer</description></item>
/// <item><term>InvalidArgs</term><description><see cref="DdbInvalidArgumentsException"/> (400, noRetry)</description></item>
/// <item><term>Retryable</term><description><see cref="DdbRetryableException"/> with its reason (transient: retried, noRetry false)</description></item>
/// <item><term>Exception with a retryable reason</term><description><see cref="DdbRetryableException"/>: the exports that predate the reason codes (DDBBuild) keep DDBERR_EXCEPTION but set the reason</description></item>
/// <item><term>Shutdown</term><description><see cref="DdbShutdownException"/> (the process is stopping)</description></item>
/// <item><term>anything else</term><description><see cref="DdbException"/></description></item>
/// </list>
/// </summary>
public static class DdbResultMapper
{
    /// <summary>The message of the last failed libddb call on this thread ("" after a success).</summary>
    public static string GetNativeLastError() => Marshal.PtrToStringUTF8(NativeDdb.DDBGetLastError()) ?? "";

    /// <summary>The reason of the last failed libddb call on this thread ("" for none).</summary>
    public static string GetNativeLastErrorReason() =>
        Marshal.PtrToStringUTF8(NativeDdb.DDBGetLastErrorReason()) ?? "";

    /// <summary>
    /// The exception of a failed call: <paramref name="message"/> and <paramref name="reason"/> are
    /// what libddb set ("" means none). Not for <see cref="DdbResult.Success"/>. A canceled call's
    /// exception carries <paramref name="token"/>, the managed token its cancel token was for.
    /// </summary>
    public static Exception Map(DdbResult result, string operation, string? message, string? reason,
        CancellationToken token = default)
    {
        if (result == DdbResult.Success)
            throw new ArgumentOutOfRangeException(nameof(result), result, "A successful call has no exception");

        var text = string.IsNullOrEmpty(message) ? $"{operation} failed ({result})" : message;

        return result switch
        {
            DdbResult.Busy => new DdbBusyException(text),
            DdbResult.BuildInProgress => new DdbBuildInProgressException(text),
            DdbResult.Canceled => new DdbCanceledException(text, null, token),
            DdbResult.NotFound => new DdbNotFoundException(text),
            DdbResult.InvalidArgs => new DdbInvalidArgumentsException(text),
            DdbResult.Retryable => new DdbRetryableException(text, string.IsNullOrEmpty(reason) ? null : reason),
            DdbResult.Shutdown => new DdbShutdownException(text),
            DdbResult.Exception when IsRetryableReason(reason) => new DdbRetryableException(text, reason),
            _ => new DdbException(text)
        };
    }

    /// <summary>
    /// A reason ABI 2.0 defines for a transient failure. ddb.h: reasons are only ever added and an
    /// unknown one is treated like none.
    /// </summary>
    public static bool IsRetryableReason(string? reason) => reason is
        DdbRetryableException.Reasons.LowDiskSpace or
        DdbRetryableException.Reasons.NoSpaceOnDevice or
        DdbRetryableException.Reasons.IdleTimeout;
}
