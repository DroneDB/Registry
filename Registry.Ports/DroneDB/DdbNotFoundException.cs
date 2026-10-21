using System;

namespace Registry.Ports.DroneDB;

/// <summary>
/// libddb found nothing for the request (native result <c>DDBERR_NOTFOUND</c>) where the adapter
/// has no "absent" answer to return: the HTTP API answers 404.
/// </summary>
public class DdbNotFoundException : DdbException
{
    public DdbNotFoundException()
    {
    }

    public DdbNotFoundException(string message) : base(message)
    {
    }

    public DdbNotFoundException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
