#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Registry.Ports.DroneDB;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// The startup handshake with libddb (XI-01, D9): the loaded library must have the ABI Registry is
/// written for (<see cref="Major"/> equal, minor at least <see cref="Minor"/>) and export every
/// function <see cref="NativeDdb"/> declares. Checked once, with <see cref="NativeLibrary"/> export
/// lookups, never by calling an export and catching <see cref="EntryPointNotFoundException"/>; a
/// mismatch fails with one message that names what is wrong and what to install.
/// </summary>
public static class DdbAbi
{
    /// <summary>
    /// <c>DDB_ABI_MAJOR</c> Registry is written for: another major cannot be used. DroneDB has one
    /// version (package.json) and the ABI follows it: libddb 2.x is ABI 2.y, a removed export is a
    /// new major.
    /// </summary>
    public const int Major = 2;

    /// <summary><c>DDB_ABI_MINOR</c> Registry needs at least (DroneDB 2.0.0).</summary>
    public const int Minor = 0;

    private const string Library = "ddb";

    private static readonly Lazy<DdbCapabilities> Verified = new(VerifyLoadedLibrary);

    /// <summary>
    /// Every export Registry calls: the entry points of the <see cref="LibraryImportAttribute"/>
    /// declarations of <see cref="NativeDdb"/>, sorted. Derived from the declarations, so a new
    /// import is checked at startup without a second list.
    /// </summary>
    public static IReadOnlyList<string> RequiredSymbols { get; } = typeof(NativeDdb)
        .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        .Select(m => (Method: m, Import: m.GetCustomAttribute<LibraryImportAttribute>()))
        .Where(x => x.Import != null)
        .Select(x => x.Import!.EntryPoint ?? x.Method.Name)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    /// <summary>Splits a <c>DDBGetAbiVersion()</c> value.</summary>
    public static (int Major, int Minor) Decode(uint version) => ((int)(version >> 16), (int)(version & 0xFFFF));

    /// <summary>
    /// Checks the loaded libddb once per process and returns its capabilities.
    /// </summary>
    /// <exception cref="DdbAbiMismatchException">
    /// The library cannot be loaded, is older than libddb 2.x, has another ABI major or lacks an export.
    /// </exception>
    public static DdbCapabilities Verify() => Verified.Value;

    private static unsafe DdbCapabilities VerifyLoadedLibrary()
    {
        // Load libddb the way every call does (a P/Invoke honours the assembly's DllImportResolver and
        // the platform probing), through an export every libddb version has; the module handle used
        // for the export lookups below is then that already-loaded library
        string version;
        try
        {
            version = Marshal.PtrToStringUTF8(NativeDdb.DDBGetVersion()) ?? "(unknown version)";
        }
        catch (DllNotFoundException ex)
        {
            throw new DdbAbiMismatchException($"libddb cannot be loaded: {ex.Message} {Requirement}", ex);
        }

        if (!NativeLibrary.TryLoad(Library, typeof(NativeDdb).Assembly, null, out var handle))
            throw new DdbAbiMismatchException($"libddb {version} is loaded but its module cannot be opened. {Requirement}");

        if (!NativeLibrary.TryGetExport(handle, nameof(NativeDdb.DDBGetAbiVersion), out var abiExport))
            throw new DdbAbiMismatchException(
                $"libddb {version} predates ABI {Major}.{Minor} (it has no DDBGetAbiVersion); Registry needs libddb {Major}.x. {Requirement}");

        var (major, minor) = RequireAbi(version, ((delegate* unmanaged<uint>)abiExport)());

        var missing = RequiredSymbols.Where(s => !NativeLibrary.TryGetExport(handle, s, out _)).ToArray();
        if (missing.Length > 0)
            throw new DdbAbiMismatchException(
                $"libddb {version} (ABI {major}.{minor}) lacks {missing.Length} export(s) Registry calls: " +
                $"{string.Join(", ", missing)}. {Requirement}");

        var json = Marshal.PtrToStringUTF8(NativeDdb.DDBGetCapabilities());
        if (string.IsNullOrEmpty(json))
            throw new DdbAbiMismatchException($"libddb {version} returned no DDBGetCapabilities() JSON (out of memory?)");

        try
        {
            return DdbCapabilities.Parse(json);
        }
        catch (FormatException ex)
        {
            throw new DdbAbiMismatchException($"libddb {version}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The ABI rule of the handshake on a <c>DDBGetAbiVersion()</c> value: the major must be
    /// <see cref="Major"/> and the minor at least <see cref="Minor"/>.
    /// </summary>
    /// <param name="version">The library version, for the message.</param>
    /// <param name="abiVersion">The <c>DDBGetAbiVersion()</c> value.</param>
    /// <exception cref="DdbAbiMismatchException">The ABI is not one Registry is written for.</exception>
    internal static (int Major, int Minor) RequireAbi(string version, uint abiVersion)
    {
        var (major, minor) = Decode(abiVersion);
        if (major != Major || minor < Minor)
            throw new DdbAbiMismatchException(
                $"libddb {version} has ABI {major}.{minor}; Registry needs libddb {Major}.x (ABI {Major}.x with x >= {Minor}). {Requirement}");
        return (major, minor);
    }

    private static string Requirement =>
        $"Install the DroneDB build Registry is pinned to (DdbVersion in Directory.Build.props; libddb {Major}.x, ABI {Major}.{Minor} or later).";
}

/// <summary>The loaded libddb cannot be used by this Registry (<see cref="DdbAbi.Verify"/>).</summary>
public sealed class DdbAbiMismatchException : Exception
{
    public DdbAbiMismatchException(string message) : base(message)
    {
    }

    public DdbAbiMismatchException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
