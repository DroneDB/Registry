using Registry.Ports.DroneDB;

namespace Registry.Adapters.Testing;

/// <summary>
/// An <see cref="IDdbRuntime"/> that never loads native libddb: the capabilities are the
/// <c>capabilities</c> contract fixture, the effective configuration the <c>effective-config</c>
/// fixture, and the lifecycle calls are recorded so a test can check the order a host makes them
/// in (libddb's rule: configuration and log sink before <see cref="RegisterProcess"/>, nothing after
/// <see cref="Shutdown"/>).
/// </summary>
public class FakeDdbRuntime : IDdbRuntime
{
    private static readonly Lazy<DdbCapabilities> RecordedCapabilities = new(() =>
        DdbCapabilities.Parse(ContractFixture.Load("capabilities").Output.ToJsonString()));

    private readonly List<string> _calls = [];
    private readonly Lock _lock = new();

    /// <summary>The lifecycle calls made so far, in order: "Configure", "SetLogSink", "RegisterProcess", "Shutdown".</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_lock)
                return [.. _calls];
        }
    }

    /// <summary>The settings of the last <see cref="Configure"/>.</summary>
    public DdbProcessSettings? Settings { get; private set; }

    /// <summary>The sink of the last <see cref="SetLogSink"/>, and its level.</summary>
    public IDdbLogSink? LogSink { get; private set; }

    public DdbLogLevel LogLevel { get; private set; }

    /// <summary>The timeout of the last <see cref="Shutdown"/>.</summary>
    public TimeSpan? ShutdownTimeout { get; private set; }

    public virtual DdbCapabilities Capabilities => RecordedCapabilities.Value;

    public virtual void Configure(DdbProcessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Record(nameof(Configure));
        Settings = settings;
    }

    private readonly Dictionary<string, string> _environment = [];

    /// <summary>
    /// The deployment environment libddb would see: <see cref="SetEnvironmentDefault"/> keeps a value set
    /// here (a test sets one to play the deployment) and records the defaults; the process environment is
    /// never changed.
    /// </summary>
    public IDictionary<string, string> Environment => _environment;

    public virtual string SetEnvironmentDefault(string name, string value)
    {
        lock (_lock)
            return _environment.TryAdd(name, value) ? value : _environment[name];
    }

    public virtual string GetEffectiveConfig() =>
        ContractFixture.Load("effective-config").Output.ToJsonString();

    public virtual void SetLogSink(IDdbLogSink? sink, DdbLogLevel minLevel)
    {
        Record(nameof(SetLogSink));
        LogSink = sink;
        LogLevel = minLevel;
    }

    public virtual void RegisterProcess(bool verbose = false) => Record(nameof(RegisterProcess));

    public virtual bool Shutdown(TimeSpan timeout)
    {
        Record(nameof(Shutdown));
        ShutdownTimeout = timeout;
        return true;
    }

    private void Record(string call)
    {
        lock (_lock)
        {
            // DDBShutdown is idempotent; every other call is refused after it
            if (call != nameof(Shutdown) && _calls.Contains(nameof(Shutdown)))
                throw new DdbShutdownException($"FakeDdbRuntime: {call} after Shutdown (libddb returns DDBERR_SHUTDOWN)");
            _calls.Add(call);
        }
    }
}
