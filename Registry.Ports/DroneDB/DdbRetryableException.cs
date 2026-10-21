using System;

namespace Registry.Ports.DroneDB;

/// <summary>
/// A transient libddb failure that says nothing about the input (native result
/// <c>DDBERR_RETRYABLE</c>, or <c>DDBERR_EXCEPTION</c> with a reason from an export older than ABI
/// 1.1): the disk is (nearly) full, or an external tool stopped producing output. The same call
/// can succeed later, so jobs retry it and the HTTP API answers 503 with <c>noRetry: false</c>.
/// </summary>
public class DdbRetryableException : DdbException
{
    /// <summary><c>DDBGetLastErrorReason()</c> values defined by ABI 1.1 (names are only ever added).</summary>
    public static class Reasons
    {
        public const string LowDiskSpace = "lowDiskSpace";
        public const string NoSpaceOnDevice = "noSpaceOnDevice";
        public const string IdleTimeout = "idleTimeout";
    }

    public DdbRetryableException()
    {
    }

    public DdbRetryableException(string message) : base(message)
    {
    }

    public DdbRetryableException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public DdbRetryableException(string message, string? reason) : base(message)
    {
        Reason = reason;
    }

    /// <summary>The machine-readable reason (<see cref="Reasons"/>), or null when libddb gave none.</summary>
    public string? Reason { get; }
}
