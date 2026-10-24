#nullable enable
using System;
using System.Runtime.InteropServices;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// Reads and writes the native process environment that libddb/GDAL read through getenv
/// (GDAL's CPLGetConfigOption falls back to getenv on every call). On Unix,
/// <see cref="Environment.SetEnvironmentVariable(string, string)"/> only changes .NET's own copy,
/// so native code would never see the value.
/// <para>
/// Not safe to call while native threads may be reading the environment: call it at startup,
/// before any native work starts.
/// </para>
/// </summary>
public static class NativeEnvironment
{
    [DllImport("libc", EntryPoint = "setenv", SetLastError = true)]
    private static extern int UnixSetEnv(string name, string value, int overwrite);

    [DllImport("libc", EntryPoint = "getenv")]
    private static extern IntPtr UnixGetEnv(string name);

    [DllImport("ucrtbase.dll", EntryPoint = "_putenv_s", CharSet = CharSet.Ansi)]
    private static extern int WindowsPutEnv(string name, string value);

    [DllImport("ucrtbase.dll", EntryPoint = "getenv", CharSet = CharSet.Ansi)]
    private static extern IntPtr WindowsGetEnv(string name);

    /// <summary>Value of <paramref name="name"/> in the native environment, or null.</summary>
    public static string? Get(string name)
    {
        var ptr = OperatingSystem.IsWindows() ? WindowsGetEnv(name) : UnixGetEnv(name);
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
    }

    /// <summary>
    /// Sets <paramref name="name"/> in the native (and managed) environment unless it already
    /// has a value. Returns false when an existing value was kept.
    /// </summary>
    public static bool SetIfUnset(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        if (!string.IsNullOrEmpty(Get(name)))
            return false;

        var rc = OperatingSystem.IsWindows() ? WindowsPutEnv(name, value) : UnixSetEnv(name, value, 0);
        if (rc != 0)
            throw new InvalidOperationException($"Cannot set native environment variable {name} (error {rc})");

        Environment.SetEnvironmentVariable(name, value);
        return true;
    }
}
