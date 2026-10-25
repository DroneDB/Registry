#nullable enable
using System;

namespace Registry.Ports.DroneDB;

/// <summary>Classification of libddb's errors shared by every caller.</summary>
public static class DdbErrors
{
    /// <summary>
    /// The call could not answer now but may on a later attempt: the index is busy, a build holds it, the disk
    /// is (nearly) full or the library is shutting down. Such an error says nothing about the data, so a caller
    /// must not read it as "absent" (the API answers 503 with a Retry-After).
    /// </summary>
    public static bool IsTransient(Exception error) =>
        error is DdbBusyException or DdbBuildInProgressException or DdbRetryableException or DdbShutdownException;
}
