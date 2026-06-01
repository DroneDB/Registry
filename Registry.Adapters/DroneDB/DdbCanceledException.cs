using System;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// Thrown when a long-running DroneDB operation was cooperatively canceled by
/// its progress callback (native result <c>DDBERR_CANCELED</c>). Distinct from
/// <see cref="DdbException"/> so callers can distinguish a user/host-initiated
/// cancellation from a genuine failure without resorting to string matching.
/// </summary>
public class DdbCanceledException : DdbException
{
    public DdbCanceledException()
    {
    }

    public DdbCanceledException(string message) : base(message)
    {
    }

    public DdbCanceledException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
