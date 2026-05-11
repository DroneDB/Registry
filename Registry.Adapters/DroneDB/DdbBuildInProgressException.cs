using System;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// Thrown when a build operation cannot proceed because another process is
/// actively building the same artifact (kernel-managed build lock is held).
/// Distinct from <see cref="DdbException"/> so callers can implement retry
/// or back-off strategies without resorting to string matching.
/// </summary>
public class DdbBuildInProgressException : DdbException
{
    public DdbBuildInProgressException()
    {
    }

    public DdbBuildInProgressException(string message) : base(message)
    {
    }

    public DdbBuildInProgressException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
