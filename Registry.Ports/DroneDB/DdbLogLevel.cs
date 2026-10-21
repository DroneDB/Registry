namespace Registry.Ports.DroneDB;

/// <summary><c>DDBLogLevel</c> (libddb ABI 1.1), in increasing severity like .NET's LogLevel.</summary>
public enum DdbLogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4,
    Fatal = 5
}
