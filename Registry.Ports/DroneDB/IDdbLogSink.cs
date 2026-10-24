namespace Registry.Ports.DroneDB;

/// <summary>
/// Receives libddb's log records and GDAL's messages (<c>DDBSetLogCallback</c>, ABI 2.0, XI-08).
/// Called on the native thread that logs, possibly on several threads at once: it must be
/// thread-safe and quick, and it must not throw (an exception is dropped) or call libddb.
/// </summary>
public interface IDdbLogSink
{
    /// <param name="level">The record's level.</param>
    /// <param name="message">The text.</param>
    /// <param name="source">"ddb" for libddb's records, "gdal" for GDAL's messages (more may be added).</param>
    /// <param name="operationId">The operation id of the call that logged (<c>DDBSetThreadOperation</c>), 0 for none.</param>
    void Write(DdbLogLevel level, string message, string source, ulong operationId);
}
