using System;

namespace Registry.Ports.DroneDB;

/// <summary>
/// libddb rejected an argument (native result <c>DDBERR_INVALIDARGS</c>, returned by the ABI 2.0
/// exports): the same call can never succeed, so the HTTP API answers 400 with <c>noRetry</c>.
/// </summary>
public class DdbInvalidArgumentsException : DdbException
{
    public DdbInvalidArgumentsException()
    {
    }

    public DdbInvalidArgumentsException(string message) : base(message)
    {
    }

    public DdbInvalidArgumentsException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
