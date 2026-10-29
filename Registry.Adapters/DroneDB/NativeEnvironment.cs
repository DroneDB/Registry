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
public static partial class NativeEnvironment
{
    // The strings are marshalled as the C runtime reads them: UTF-8 on Unix, the ANSI code page on Windows
    // (the narrow getenv/_putenv_s that libddb and GDAL use; LibraryImport has no ANSI marshalling, so the
    // Windows entry points stay DllImport with the conversion spelled out).
    [LibraryImport("libc", EntryPoint = "setenv", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnixSetEnv(string name, string value, int overwrite);

    [LibraryImport("libc", EntryPoint = "unsetenv", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnixUnsetEnv(string name);

    [LibraryImport("libc", EntryPoint = "getenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr UnixGetEnv(string name);

    [DllImport("ucrtbase.dll", EntryPoint = "_putenv_s", BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int WindowsPutEnv([MarshalAs(UnmanagedType.LPStr)] string name,
        [MarshalAs(UnmanagedType.LPStr)] string value);

    [DllImport("ucrtbase.dll", EntryPoint = "getenv", BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr WindowsGetEnv([MarshalAs(UnmanagedType.LPStr)] string name);

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
        EnsureSet(name, rc);

        Environment.SetEnvironmentVariable(name, value);
        return true;
    }

    /// <summary>
    /// Sets <paramref name="name"/> to <paramref name="value"/> in the native and managed environment, or removes
    /// it when <paramref name="value"/> is null, whatever it was before.
    /// </summary>
    public static void Set(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Environment.SetEnvironmentVariable(name, value);
        // _putenv_s with an empty value removes the variable on Windows.
        var rc = OperatingSystem.IsWindows() ? WindowsPutEnv(name, value ?? "") :
            value == null ? UnixUnsetEnv(name) : UnixSetEnv(name, value, 1);
        EnsureSet(name, rc);
    }

    private static void EnsureSet(string name, int rc)
    {
        if (rc != 0)
            throw new InvalidOperationException($"Cannot set native environment variable {name} (error {rc})");
    }
}
