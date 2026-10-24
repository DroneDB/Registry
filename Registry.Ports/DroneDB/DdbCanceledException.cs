using System;
using System.Threading;

namespace Registry.Ports.DroneDB;

/// <summary>
/// A DroneDB call stopped because it was canceled (native result <c>DDBERR_CANCELED</c>: its
/// progress callback or its cancel token asked it to). It is an
/// <see cref="OperationCanceledException"/>, not a <see cref="DdbException"/>: a cancellation is
/// not a failure, so it is never wrapped or counted as one, and code that handles cancellation
/// handles it.
/// </summary>
public class DdbCanceledException : OperationCanceledException
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

    public DdbCanceledException(string message, CancellationToken token) : base(message, token)
    {
    }

    public DdbCanceledException(string message, Exception? innerException, CancellationToken token)
        : base(message, innerException, token)
    {
    }
}
