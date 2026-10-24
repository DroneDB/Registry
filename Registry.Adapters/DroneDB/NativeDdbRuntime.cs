#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text.Json.Nodes;
using Registry.Ports.DroneDB;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// <see cref="IDdbRuntime"/> over libddb ABI 2.0. The state it changes (configuration, log
/// callback, initialization, shutdown) belongs to the process, so every instance shares it.
/// </summary>
public sealed unsafe class NativeDdbRuntime : IDdbRuntime
{
    private static readonly Lock LogLock = new();
    private static GCHandle _logSink;

    public DdbCapabilities Capabilities => DdbAbi.Verify();

    public void Configure(DdbProcessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var json = ToConfigureJson(settings);
        NativeCall.Invoke(nameof(NativeDdb.DDBConfigure), () => NativeDdb.DDBConfigure(json));
    }

    /// <summary>The <c>DDBConfigure</c> JSON of <paramref name="settings"/>: only the values that are set.</summary>
    public static string ToConfigureJson(DdbProcessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var json = new JsonObject();
        if (settings.CpuPoolSize is { } cpuPoolSize)
            json["cpuPoolSize"] = cpuPoolSize;
        if (settings.IoThreads is { } ioThreads)
            json["ioThreads"] = ioThreads;
        if (settings.MinFreeDiskMb is { } minFreeDiskMb)
            json["minFreeDiskMb"] = minFreeDiskMb;
        if (!string.IsNullOrEmpty(settings.CacheDir))
            json["cacheDir"] = settings.CacheDir;
        return json.ToJsonString(NativeJson.Options);
    }

    public string SetEnvironmentDefault(string name, string value) =>
        NativeEnvironment.SetIfUnset(name, value) ? value : NativeEnvironment.Get(name) ?? value;

    public string GetEffectiveConfig() =>
        NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBGetEffectiveConfig),
            (out IntPtr output) => NativeDdb.DDBGetEffectiveConfig(out output));

    public void SetLogSink(IDdbLogSink? sink, DdbLogLevel minLevel)
    {
        lock (LogLock)
        {
            var next = sink == null ? default : GCHandle.Alloc(sink);
            try
            {
                NativeCall.Invoke(nameof(NativeDdb.DDBSetLogCallback), () => sink == null
                    ? NativeDdb.DDBSetLogCallback(null, 0, IntPtr.Zero)
                    : NativeDdb.DDBSetLogCallback(&OnLog, (int)minLevel, GCHandle.ToIntPtr(next)));
            }
            catch
            {
                if (next.IsAllocated)
                    next.Free();
                throw;
            }

            // DDBSetLogCallback returned: the previous callback is not called any more
            if (_logSink.IsAllocated)
                _logSink.Free();
            _logSink = next;
        }
    }

    public void RegisterProcess(bool verbose = false) =>
        NativeCall.Invoke(nameof(NativeDdb.DDBRegisterProcess), () => NativeDdb.DDBRegisterProcess(verbose));

    public bool Shutdown(TimeSpan timeout)
    {
        var ms = timeout < TimeSpan.Zero ? -1 : (int)Math.Min(int.MaxValue, timeout.TotalMilliseconds);
        var result = NativeDdb.DDBShutdown(ms);
        switch (result)
        {
            case DdbResult.Success:
                ReleaseLogSink();
                return true;
            case DdbResult.Busy:
                // Shutting down: new calls are refused and the log callback is unregistered, the
                // calls still running keep their connections
                ReleaseLogSink();
                return false;
            default:
                NativeCall.Check(result, nameof(NativeDdb.DDBShutdown));
                return false;
        }
    }

    private static void ReleaseLogSink()
    {
        lock (LogLock)
        {
            if (_logSink.IsAllocated)
                _logSink.Free();
            _logSink = default;
        }
    }

    /// <summary>
    /// <c>DDBLogCallback</c>: called on the native thread that logs, possibly on several at once.
    /// Never throws (XI-03): a sink failure is dropped.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLog(int level, byte* message, byte* source, ulong opId, IntPtr userData)
    {
        try
        {
            if (userData == IntPtr.Zero || GCHandle.FromIntPtr(userData).Target is not IDdbLogSink sink)
                return;

            sink.Write((DdbLogLevel)level,
                Marshal.PtrToStringUTF8((IntPtr)message) ?? "",
                Marshal.PtrToStringUTF8((IntPtr)source) ?? "",
                opId);
        }
        catch
        {
            // Nothing may leave an UnmanagedCallersOnly method; a log record is not worth the process
        }
    }
}
