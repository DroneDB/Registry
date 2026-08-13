namespace Registry.Adapters.DroneDB;

/// <summary>
/// Transient DroneDB write contention (native SQLITE_BUSY/SQLITE_LOCKED, DDBERR_BUSY).
/// Safe to retry: the operation did not corrupt state, it simply could not acquire the
/// write lock within the native retry budget. See ImproveParallelWrites plan workstream 03.
/// </summary>
public class DdbBusyException : DdbException
{
    public DdbBusyException()
    {
    }

    public DdbBusyException(string message) : base(message)
    {
    }

    public DdbBusyException(string message, System.Exception innerException) : base(message, innerException)
    {
    }
}
