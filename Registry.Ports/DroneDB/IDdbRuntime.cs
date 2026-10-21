using System;

namespace Registry.Ports.DroneDB;

/// <summary>
/// The libddb process lifecycle (ABI 1.1, XI-01/XI-06/XI-07/XI-08): the capabilities of the loaded
/// library, the process configuration and the log sink (both set before
/// <see cref="RegisterProcess"/>), the one-time initialization and the shutdown. Kept apart from
/// <see cref="IDdbWrapper"/> (dataset operations): only the host's startup and shutdown use it.
/// </summary>
public interface IDdbRuntime
{
    /// <summary>What the loaded library supports, checked against the ABI Registry is written for.</summary>
    DdbCapabilities Capabilities { get; }

    /// <summary>Applies <paramref name="settings"/> (<c>DDBConfigure</c>); call it before <see cref="RegisterProcess"/>.</summary>
    /// <exception cref="DdbInvalidArgumentsException">libddb rejected a value; nothing was changed.</exception>
    void Configure(DdbProcessSettings settings);

    /// <summary>The settings in effect and where each comes from (<c>DDBGetEffectiveConfig</c>), as JSON.</summary>
    string GetEffectiveConfig();

    /// <summary>
    /// Sends libddb's log records of <paramref name="minLevel"/> and above to <paramref name="sink"/>
    /// instead of stdout (<c>DDBSetLogCallback</c>); null sends them to stdout again. Set before
    /// <see cref="RegisterProcess"/> to receive DEBUG and TRACE records.
    /// </summary>
    void SetLogSink(IDdbLogSink? sink, DdbLogLevel minLevel);

    /// <summary>
    /// Initializes libddb (<c>DDBRegisterProcess</c>); idempotent. A failed initialization is
    /// final for the process.
    /// </summary>
    /// <exception cref="DdbException">The initialization failed.</exception>
    void RegisterProcess(bool verbose = false);

    /// <summary>
    /// Shuts libddb down (<c>DDBShutdown</c>): refuses new calls, waits up to
    /// <paramref name="timeout"/> for the calls in flight, checkpoints and closes the index
    /// connections and flushes the log. True when the library is shut down; false when calls were
    /// still running after the timeout (the exit-time checkpoint covers the idle connections).
    /// </summary>
    bool Shutdown(TimeSpan timeout);
}
