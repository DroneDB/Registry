namespace Registry.Adapters.DroneDB;

public enum DdbResult
{
    Success = 0, // No error
    Exception = 1, // Generic app exception
    BuildDependencyMissing = 2,
    BuildInProgress = 3 // Another process holds the build lock
};