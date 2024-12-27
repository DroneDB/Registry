namespace Registry.Adapters.DroneDB;

public enum DdbResult
{
    Success = 0, // No error
    Exception = 1, // Generic app exception
    BuildDependencyMissing = 2
};