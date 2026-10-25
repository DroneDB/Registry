#nullable enable
using Registry.Ports.DroneDB;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// Hands the current <see cref="DdbOperation"/> to libddb right before a native call
/// (<c>DDBSetThreadOperation</c>, ABI 2.0). libddb keeps it for the next call on the
/// thread only, until that call returns, so it is set before each call of an operation: an async
/// job hops between pool threads, and every call it makes carries its own id. Outside an
/// operation nothing is called (the previous call's id ended with it).
/// </summary>
internal static class NativeOperation
{
    /// <summary>Gives the next native call on this thread the current operation id. Never throws.</summary>
    internal static void Bind()
    {
        var current = DdbOperation.Current;
        if (current == 0)
            return;

        // The result is always success for a library with the export (the startup handshake rules
        // out one without); an operation id is a log annotation, never a reason to fail the call
        NativeDdb.DDBSetThreadOperation(current);
    }
}
