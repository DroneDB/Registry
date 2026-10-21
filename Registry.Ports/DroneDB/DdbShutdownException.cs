using System;

namespace Registry.Ports.DroneDB;

/// <summary>
/// libddb refused the call because the process is shutting down (native result
/// <c>DDBERR_SHUTDOWN</c>, after <c>DDBShutdown</c>). Transient for a client: another instance (or
/// this one after a restart) can serve the request.
/// </summary>
public class DdbShutdownException : DdbException
{
    public DdbShutdownException()
    {
    }

    public DdbShutdownException(string message) : base(message)
    {
    }

    public DdbShutdownException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
