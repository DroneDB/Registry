#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// P/Invoke declarations of the libddb exports added in ABI 1.1 (XI-01; see ddb.h and ddb.symbols
/// of DroneDB 1.13). Not used by <see cref="NativeDdbWrapper"/> yet: Registry still runs on libddb
/// 1.12.2 (ABI 1.0), and RQ-08 adopts these exports behind <c>IDdbWrapper</c>. Today only the
/// Registry.Adapters.Ddb.Test contract tests call them, through the declarations below, the way
/// RQ-08 will: UTF-8 strings, 1-byte bools, outputs freed with <see cref="DDBFree"/> (an ABI 1.0
/// export, declared here too so these declarations are self-contained).
/// </summary>
/// <remarks>
/// Each <see cref="LibraryImportAttribute"/> stub binds its export on its first call, not when
/// this class or the library is loaded, so these declarations are safe with a library that lacks
/// them. A caller checks <see cref="DDBGetAbiVersion"/> first (<see cref="DdbAbi"/>): it throws
/// <see cref="EntryPointNotFoundException"/> on ABI 1.0.
/// </remarks>
internal static unsafe partial class NativeDdbAbi11
{
    private const string Library = "ddb";

    /// <summary>Frees a string output of libddb (ABI 1.0).</summary>
    [LibraryImport(Library)]
    internal static partial DdbResult DDBFree(IntPtr ptr);

    /// <summary><c>(major &lt;&lt; 16) | minor</c> of the loaded library. Exempt: callable at any time.</summary>
    [LibraryImport(Library)]
    internal static partial uint DDBGetAbiVersion();

    /// <summary>The capabilities JSON, owned by the library for the life of the process (do not free).</summary>
    [LibraryImport(Library)]
    internal static partial IntPtr DDBGetCapabilities();

    /// <summary>The machine-readable reason of the last failed call on this thread, owned by the library.</summary>
    [LibraryImport(Library)]
    internal static partial IntPtr DDBGetLastErrorReason();

    /// <summary>ABI 1.1 returns <see cref="DdbResult"/> (ABI 1.0 returned void).</summary>
    [LibraryImport(Library)]
    internal static partial DdbResult DDBRegisterProcess([MarshalAs(UnmanagedType.U1)] bool verbose);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBNormalizePath(string? path, string? reservedJson, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBStacData(string? ddbPath, string? entry, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBStacItemCollectionData(string? ddbPath, string? bbox, string? datetime,
        int limit, int offset, out IntPtr output);

    [LibraryImport(Library)]
    internal static partial DdbResult DDBGetEntryTypes(out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGetBuildArtifacts(string? ddbPath, string?[]? paths, int numPaths,
        out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBResolveBuildFile(string? ddbPath, string? hash, string? path,
        out IntPtr outPath);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBCopyBuildArtifacts(string? sourceDdbPath, string? destinationDdbPath,
        string? hash, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBListEntriesEx(string? ddbPath, string? folder, string? namePattern,
        string? types, in DdbListEntriesOptions options, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBConfigure(string? configJson);

    [LibraryImport(Library)]
    internal static partial DdbResult DDBGetEffectiveConfig(out IntPtr output);

    /// <summary>Process-final: no call but the exempt accessors works after it.</summary>
    [LibraryImport(Library)]
    internal static partial DdbResult DDBShutdown(int timeoutMs);

    [LibraryImport(Library)]
    internal static partial DdbResult DDBCreateCancelToken(out IntPtr token);

    [LibraryImport(Library)]
    internal static partial DdbResult DDBCancel(IntPtr token);

    [LibraryImport(Library)]
    internal static partial DdbResult DDBFreeCancelToken(IntPtr token);

    [LibraryImport(Library)]
    internal static partial DdbResult DDBSetThreadCancelToken(IntPtr token);

    /// <summary>
    /// <c>DDBLogCallback</c>: <c>void (*)(int level, const char *message, const char *source,
    /// uint64_t opId, void *userData)</c>, the platform's default C calling convention.
    /// </summary>
    [LibraryImport(Library)]
    internal static partial DdbResult DDBSetLogCallback(
        delegate* unmanaged[Cdecl]<int, byte*, byte*, ulong, IntPtr, void> callback, int minLevel, IntPtr userData);

    [LibraryImport(Library)]
    internal static partial DdbResult DDBSetThreadOperation(ulong opId);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBApplyDeltaEx(string? delta, string? sourcePath, string? ddbPath,
        int mergeStrategy, string? sourceMetaDump, string? verifiedHashesJson, out IntPtr conflicts);
}

/// <summary>The ABI version handshake (XI-01): <c>DDB_ABI_MAJOR</c>/<c>DDB_ABI_MINOR</c> of ddb.h.</summary>
internal static class DdbAbi
{
    /// <summary>The ABI version these declarations are written for.</summary>
    internal const int Major = 1;

    internal const int Minor = 1;

    /// <summary>Splits a <c>DDBGetAbiVersion()</c> value.</summary>
    internal static (int Major, int Minor) Decode(uint version) => ((int)(version >> 16), (int)(version & 0xFFFF));

    /// <summary>
    /// The ABI version of the loaded library, or <c>null</c> when it has no <c>DDBGetAbiVersion</c>
    /// (ABI 1.0, libddb 1.12.x and older).
    /// </summary>
    internal static (int Major, int Minor)? Loaded()
    {
        try
        {
            return Decode(NativeDdbAbi11.DDBGetAbiVersion());
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }
}

/// <summary>
/// <c>DDBListEntriesOptions</c> (ABI 1.1): 8 bytes, <c>structSize</c> at 0, then three 1-byte
/// bools at 4, 5 and 6, and one byte of padding (keep it zero). A later ABI appends fields after
/// the 8 bytes and raises <c>structSize</c>; a library rejects a struct whose bytes past the size
/// it knows are not zero (DDBERR_INVALIDARGS).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DdbListEntriesOptions
{
    internal uint StructSize;
    internal byte Recursive;
    internal byte WithDetails;
    internal byte WithBuild;
    internal byte Reserved;

    internal static DdbListEntriesOptions Create(bool recursive, bool withDetails, bool withBuild) => new()
    {
        StructSize = (uint)Unsafe.SizeOf<DdbListEntriesOptions>(),
        Recursive = recursive ? (byte)1 : (byte)0,
        WithDetails = withDetails ? (byte)1 : (byte)0,
        WithBuild = withBuild ? (byte)1 : (byte)0
    };
}

/// <summary><c>DDBLogLevel</c> (ABI 1.1), in increasing severity like .NET's LogLevel.</summary>
internal enum DdbLogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4,
    Fatal = 5
}
