namespace Registry.Adapters.DroneDB;

public enum DdbResult
{
    Success = 0, // No error
    Exception = 1, // Generic app exception
    BuildDependencyMissing = 2,
    BuildInProgress = 3, // Another process holds the build lock
    Canceled = 4, // Operation canceled by the progress callback
    Busy = 5, // Transient DB contention (SQLITE_BUSY/LOCKED); safe to retry
    NotFound = 6, // The requested entry does not exist
    // ABI 1.1 (libddb 1.13): returned by the exports added in ABI 1.1 only (see NativeDdbAbi11),
    // DDBERR_SHUTDOWN by every export once DDBShutdown started
    InvalidArgs = 7, // An argument is invalid: fix the call, do not retry
    Retryable = 8, // A transient failure (DDBGetLastErrorReason): retry later
    Shutdown = 9 // The library is shut down (DDBShutdown): no call is accepted
};