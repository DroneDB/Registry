#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Ports;
using Registry.Ports.DroneDB;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// P/Invoke wrapper for the native DroneDB C API.
/// </summary>
public class NativeDdbWrapper : IDdbWrapper
{
    [DllImport("ddb", EntryPoint = "DDBRegisterProcess")]
    private static extern void _RegisterProcess(bool verbose = false);

    public NativeDdbWrapper()
    {
    }

    public NativeDdbWrapper(bool verbose)
    {
        _RegisterProcess(verbose);
    }

    public void RegisterProcess(bool verbose = false)
    {
        _RegisterProcess(verbose);
    }

    public string TileMimeType { get; } = "image/png";
    public string ThumbnailMimeType { get; } = "image/webp";

    [DllImport("ddb", EntryPoint = "DDBGetVersion")]
    private static extern IntPtr _GetVersion();

    public string GetVersion()
    {
        var ptr = _GetVersion();

        var res = Marshal.PtrToStringUTF8(ptr);

        if (string.IsNullOrWhiteSpace(res))
            throw new DdbException("Unable to get version");

        return res;
    }

    // Thin forwarders: implementation lives in DdbResultMapper (public static, unit-testable
    // without InternalsVisibleTo); call sites in this class keep their unqualified names.
    private static string SafeGetLastError(string? operation = null) => DdbResultMapper.SafeGetLastError(operation);

    private static void ThrowForFinalResult(DdbResult result, string operation) => DdbResultMapper.ThrowForFinalResult(result, operation);

    [DllImport("ddb", EntryPoint = "DDBInit")]
    private static extern DdbResult _Init([MarshalAs(UnmanagedType.LPUTF8Str)] string directory, out IntPtr outPath);

    public string Init(string directory)
    {
        DdbResult result;
        try
        {
            result = _Init(directory, out var outPath);
            if (result == DdbResult.Success)
            {
                var res = MarshalAndFreeUtf8(outPath);

                if (string.IsNullOrWhiteSpace(res))
                    throw new DdbException("Unable to init");

                return res;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("init")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "init");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("init"));
    }

    /// <summary>
    /// Allocates an array of IntPtr, each pointing to a null-terminated UTF-8 encoded copy of the corresponding string.
    /// The caller must free each pointer with Marshal.FreeHGlobal after use.
    /// </summary>
    private static IntPtr[] MarshalStringArrayToUtf8(string[] strings)
    {
        var ptrs = new IntPtr[strings.Length];
        for (var i = 0; i < strings.Length; i++)
        {
            var bytes = Encoding.UTF8.GetBytes(strings[i] + '\0');
            ptrs[i] = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, ptrs[i], bytes.Length);
        }
        return ptrs;
    }

    private static void FreeUtf8StringArray(IntPtr[] ptrs)
    {
        foreach (var ptr in ptrs)
        {
            if (ptr != IntPtr.Zero)
                Marshal.FreeHGlobal(ptr);
        }
    }

    private static string? MarshalAndFreeUtf8(IntPtr ptr)
    {
        var str = Marshal.PtrToStringUTF8(ptr);
        _DDBFree(ptr);
        return str;
    }

    [DllImport("ddb", EntryPoint = "DDBAdd")]
    private static extern DdbResult _Add([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        IntPtr[] paths,
        int numPaths, out IntPtr output, bool recursive);

    public List<Entry> Add(string ddbPath, string path, bool recursive = false)
    {
        return Add(ddbPath, [path], recursive);
    }

    public List<Entry> Add(string ddbPath, string[] paths, bool recursive = false)
    {
        paths = paths.Select(p => p?.Replace('\\', '/')).ToArray();
        var utf8Ptrs = MarshalStringArrayToUtf8(paths);
        var result = DdbResult.Exception;
        try
        {
            result = _Add(ddbPath, utf8Ptrs, paths.Length, out var output, recursive);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);

                if (string.IsNullOrWhiteSpace(json))
                    throw new DdbException("Unable to add");

                var res = JsonConvert.DeserializeObject<List<Entry>>(json);

                if (res == null)
                    throw new InvalidOperationException($"Unable to deserialize add result: {json}");

                return res;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("add")}\", check inner exception for details",
                ex);
        }
        finally
        {
            FreeUtf8StringArray(utf8Ptrs);
        }

        // Non-success results map to typed exceptions (Busy → DdbBusyException) so callers can
        // retry instead of matching on a message string.
        ThrowForFinalResult(result, "add");

        // Unreachable in practice (Success returns above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("add"));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeAddOptions
    {
        [MarshalAs(UnmanagedType.I1)] public bool Recursive;
        [MarshalAs(UnmanagedType.I1)] public bool StopOnError;
        public int MaxConflictRetries;
    }

    [DllImport("ddb", EntryPoint = "DDBAddWithOptions")]
    private static extern DdbResult _AddWithOptions([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        IntPtr[] paths, int numPaths, ref NativeAddOptions options, out IntPtr output);

    public BatchAddResult AddWithOptions(string ddbPath, string[] paths, bool stopOnError = false)
    {
        paths = paths.Select(p => p?.Replace('\\', '/')).ToArray();
        var utf8Ptrs = MarshalStringArrayToUtf8(paths);
        // Fixed policy (not caller-configurable): never recurse (a recursive native walk would
        // report paths the caller never provided, breaking the completeness contract) and keep
        // the native conflict-retry budget at its default of 2. The NativeAddOptions struct and
        // the DDBAddWithOptions ABI are unchanged.
        var options = new NativeAddOptions
        {
            Recursive = false, StopOnError = stopOnError, MaxConflictRetries = 2
        };
        var result = DdbResult.Exception;
        try
        {
            result = _AddWithOptions(ddbPath, utf8Ptrs, paths.Length, ref options, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);

                if (string.IsNullOrWhiteSpace(json))
                    throw new DdbException("Unable to add");

                var res = JsonConvert.DeserializeObject<BatchAddResult>(json);

                if (res == null)
                    throw new InvalidOperationException($"Unable to deserialize add result: {json}");

                return res;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("addWithOptions")}\", check inner exception for details",
                ex);
        }
        finally
        {
            FreeUtf8StringArray(utf8Ptrs);
        }

        ThrowForFinalResult(result, "addWithOptions");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("addWithOptions"));
    }

    [DllImport("ddb", EntryPoint = "DDBRemove")]
    private static extern DdbResult _Remove([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        IntPtr[] paths,
        int numPaths);

    public void Remove(string ddbPath, string path)
    {
        Remove(ddbPath, [path]);
    }

    public void Remove(string ddbPath, string[] paths)
    {
        paths = paths.Select(p => p?.Replace('\\', '/')).ToArray();
        var utf8Ptrs = MarshalStringArrayToUtf8(paths);
        DdbResult result;
        try
        {
            result = _Remove(ddbPath, utf8Ptrs, paths?.Length ?? 0);
            if (result == DdbResult.Success) return;
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError()}\", check inner exception for details",
                ex);
        }
        finally
        {
            FreeUtf8StringArray(utf8Ptrs);
        }

        ThrowForFinalResult(result, "remove");
    }

    [DllImport("ddb", EntryPoint = "DDBInfo")]
    private static extern DdbResult _Info(
        IntPtr[] paths,
        int numPaths,
        out IntPtr output,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string format, bool recursive = false,
        int maxRecursionDepth = 0, [MarshalAs(UnmanagedType.LPUTF8Str)] string geometry = "auto",
        bool withHash = false, bool stopOnError = true);

    public List<Entry> Info(string path, bool recursive = false, int maxRecursionDepth = 0,
        bool withHash = false)
    {
        return Info([path], recursive, maxRecursionDepth, withHash);
    }

    public List<Entry> Info(string[] paths, bool recursive = false, int maxRecursionDepth = 0,
        bool withHash = false)
    {
        var utf8Ptrs = MarshalStringArrayToUtf8(paths);
        DdbResult result;
        try
        {
            result = _Info(utf8Ptrs, paths?.Length ?? 0, out var output, "json", recursive, maxRecursionDepth, "auto",
                                    withHash);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);

                if (string.IsNullOrWhiteSpace(json))
                    throw new DdbException("Unable get info");

                var res = JsonConvert.DeserializeObject<List<Entry>>(json);

                if (res == null)
                    throw new InvalidOperationException($"Unable to deserialize info result: {json}");

                return res;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }

        catch (DdbException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("info")}\", check inner exception for details",
                ex);
        }
        finally
        {
            FreeUtf8StringArray(utf8Ptrs);
        }

        ThrowForFinalResult(result, "info");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("info"));
    }

    [DllImport("ddb", EntryPoint = "DDBList")]
    private static extern DdbResult _List([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        IntPtr[] paths,
        int numPaths,
        out IntPtr output,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string format,
        bool recursive,
        int maxRecursionDepth = 0);

    public List<Entry> List(string ddbPath, string path, bool recursive = false, int maxRecursionDepth = 0)
    {
        return List(ddbPath, [path], recursive, maxRecursionDepth);
    }

    public List<Entry> List(string ddbPath, string[] paths, bool recursive = false, int maxRecursionDepth = 0)
    {
        if (paths.Length == 0)
            throw new ArgumentException("Paths is empty");

        paths = paths.Select(item => item.Replace('\\', '/')).ToArray();
        var utf8Ptrs = MarshalStringArrayToUtf8(paths);
        DdbResult lst;
        try
        {
            lst = _List(ddbPath, utf8Ptrs, paths.Length, out var output, "json", recursive, maxRecursionDepth);

            if (lst == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);

                if (string.IsNullOrWhiteSpace(json))
                    throw new InvalidOperationException("Unable get list");

                var res = JsonConvert.DeserializeObject<List<Entry>>(json);

                if (res == null)
                    throw new InvalidOperationException($"Unable to deserialize list result: {json}");

                return res;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError()}\", check inner exception for details",
                ex);
        }
        finally
        {
            FreeUtf8StringArray(utf8Ptrs);
        }

        ThrowForFinalResult(lst, "list");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("list"));
    }

    [DllImport("ddb", EntryPoint = "DDBAppendPassword")]
    private static extern DdbResult _AppendPassword(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password);

    public void AppendPassword(string ddbPath, string password)
    {
        DdbResult result;
        try
        {
            result = _AppendPassword(ddbPath, password);
            if (result == DdbResult.Success) return;
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("append password")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "append password");
    }

    [DllImport("ddb", EntryPoint = "DDBVerifyPassword")]
    private static extern DdbResult _VerifyPassword(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
        out bool verified);

    public bool VerifyPassword(string ddbPath, string password)
    {
        DdbResult result;
        try
        {
            result = _VerifyPassword(ddbPath, password, out var res);
            if (result == DdbResult.Success) return res;
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("verify password")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "verify password");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("verify password"));
    }

    [DllImport("ddb", EntryPoint = "DDBClearPasswords")]
    private static extern DdbResult _ClearPasswords(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath);

    public void ClearPasswords(string ddbPath)
    {
        DdbResult result;
        try
        {
            result = _ClearPasswords(ddbPath);
            if (result == DdbResult.Success) return;
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError()}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "clear passwords");
    }

    [DllImport("ddb", EntryPoint = "DDBChattr")]
    private static extern DdbResult _ChangeAttributes(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath, [MarshalAs(UnmanagedType.LPUTF8Str)] string attributesJson,
        out IntPtr jsonOutput);

    public Dictionary<string, object> ChangeAttributes(string ddbPath, Dictionary<string, object> attributes)
    {
        if (attributes == null)
            throw new ArgumentException("Attributes is null");

        DdbResult result;
        try
        {
            var attrs = JsonConvert.SerializeObject(attributes);

            result = _ChangeAttributes(ddbPath, attrs, out var output);
            if (result == DdbResult.Success)
            {
                var res = MarshalAndFreeUtf8(output);

                if (string.IsNullOrWhiteSpace(res))
                    throw new InvalidOperationException("Unable get attributes");

                var rs = JsonConvert.DeserializeObject<Dictionary<string, object>>(res);

                if (rs == null)
                    throw new InvalidOperationException($"Unable to deserialize attributes result: {res}");

                return rs;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("change attributes")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "change attributes");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("change attributes"));
    }

    public Dictionary<string, object> GetAttributes(string ddbPath)
    {
        return ChangeAttributes(ddbPath, new Dictionary<string, object>());
    }

    [DllImport("ddb", EntryPoint = "DDBGenerateThumbnail")]
    private static extern DdbResult _GenerateThumbnail(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filePath, int size, [MarshalAs(UnmanagedType.LPUTF8Str)] string destPath);

    public void GenerateThumbnail(string filePath, int size, string destPath)
    {
        if (filePath == null)
            throw new ArgumentException("filePath is null");

        if (destPath == null)
            throw new ArgumentException("destPath is null");

        if (size <= 0)
            throw new ArgumentException("size must be positive");

        // Validate file exists before calling native code to prevent segfault
        if (!File.Exists(filePath))
            throw new DdbException($"File not found: '{filePath}'. Cannot generate thumbnail for non-existent file.");

        DdbResult result;
        try
        {
            result = _GenerateThumbnail(filePath, size, destPath);
            if (result == DdbResult.Success) return;
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("generate thumbnail")}\", check inner exception for details",
                ex);
        }

        // Preserving the file context in the message (generic mapper drops call-site context).
        if (result == DdbResult.Busy)
            throw new DdbBusyException($"{SafeGetLastError("generate thumbnail")} (file: '{filePath}', size: {size}, dest: '{destPath}')");

        throw new DdbException($"{SafeGetLastError("generate thumbnail")} (file: '{filePath}', size: {size}, dest: '{destPath}')");
    }

    [DllImport("ddb", EntryPoint = "DDBVSIFree")]
    private static extern DdbResult _DDBVSIFree(
        IntPtr buffer);

    [DllImport("ddb", EntryPoint = "DDBFree")]
    private static extern DdbResult _DDBFree(IntPtr ptr);

    [DllImport("ddb", EntryPoint = "DDBGenerateMemoryThumbnail")]
    private static extern DdbResult _GenerateMemoryThumbnail(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filePath, int size, out IntPtr outBuffer, out int outBufferSize);

    public byte[] GenerateThumbnail(string filePath, int size)
    {
        if (filePath == null)
            throw new ArgumentException("filePath is null");

        if (size <= 0)
            throw new ArgumentException("size must be positive");

        // Validate file exists before calling native code to prevent segfault
        if (!File.Exists(filePath))
            throw new DdbException($"File not found: '{filePath}'. Cannot generate thumbnail for non-existent file.");

        DdbResult result;
        try
        {
            result = _GenerateMemoryThumbnail(filePath, size, out var outBuffer, out var outBufferSize);
            if (result == DdbResult.Success)
            {
                var destBuf = new byte[outBufferSize];
                Marshal.Copy(outBuffer, destBuf, 0, outBufferSize);

                _DDBVSIFree(outBuffer);

                return destBuf;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("generate memory thumbnail")}\", check inner exception for details",
                ex);
        }

        // Preserving the file context in the message (generic mapper drops call-site context).
        if (result == DdbResult.Busy)
            throw new DdbBusyException($"{SafeGetLastError("generate memory thumbnail")} (file: '{filePath}', size: {size})");

        throw new DdbException($"{SafeGetLastError("generate memory thumbnail")} (file: '{filePath}', size: {size})");
    }

    [DllImport("ddb", EntryPoint = "DDBTile")]
    private static extern DdbResult _GenerateTile(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string inputPath, int tz, int tx, int ty, out IntPtr outputTilePath,
        int tileSize, bool tms, bool forceRecreate);

    public string GenerateTile(string inputPath, int tz, int tx, int ty, int tileSize, bool tms,
        bool forceRecreate = false)
    {
        if (inputPath == null)
            throw new ArgumentException("inputPath is null");

        DdbResult result;
        try
        {
            result = _GenerateTile(inputPath, tz, tx, ty, out var output, tileSize, tms, forceRecreate);
            if (result == DdbResult.Success)
            {
                var res = MarshalAndFreeUtf8(output);

                if (string.IsNullOrWhiteSpace(res))
                    throw new DdbException("Unable get tile path");

                return res;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("generate tile")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "generate tile");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("generate tile"));
    }

    [DllImport("ddb", EntryPoint = "DDBMemoryTile")]
    private static extern DdbResult _GenerateMemoryTile(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string inputPath, int tz, int tx, int ty, out IntPtr outBuffer,
        out int outBufferSize, int tileSize, bool tms, bool forceRecreate,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string inputPathHash);

    public byte[] GenerateMemoryTile(string inputPath, int tz, int tx, int ty, int tileSize, bool tms,
        bool forceRecreate = false, string inputPathHash = "")
    {
        if (inputPath == null)
            throw new ArgumentException("inputPath is null");

        DdbResult result;
        try
        {
            result = _GenerateMemoryTile(inputPath, tz, tx, ty, out var outBuffer, out var outBufferSize, tileSize, tms,
                                    forceRecreate, inputPathHash);
            if (result == DdbResult.Success)
            {
                var destBuf = new byte[outBufferSize];
                Marshal.Copy(outBuffer, destBuf, 0, outBufferSize);

                _DDBVSIFree(outBuffer);

                return destBuf;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("generate memory tile")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "generate memory tile");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("generate memory tile"));
    }

    [DllImport("ddb", EntryPoint = "DDBMemoryTileFmt")]
    private static extern DdbResult _GenerateMemoryTileFmt(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string inputPath, int tz, int tx, int ty, out IntPtr outBuffer,
        out int outBufferSize, int tileSize, bool tms, bool forceRecreate,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string inputPathHash,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string outputFormat);

    public byte[] GenerateMemoryTile(string inputPath, int tz, int tx, int ty, int tileSize, bool tms,
        bool forceRecreate, string inputPathHash, string outputFormat)
    {
        if (inputPath == null)
            throw new ArgumentException("inputPath is null");

        DdbResult result;
        try
        {
            result = _GenerateMemoryTileFmt(inputPath, tz, tx, ty, out var outBuffer, out var outBufferSize, tileSize, tms,
                                    forceRecreate, inputPathHash ?? string.Empty, outputFormat ?? "png");
            if (result == DdbResult.Success)
            {
                var destBuf = new byte[outBufferSize];
                Marshal.Copy(outBuffer, destBuf, 0, outBufferSize);
                _DDBVSIFree(outBuffer);
                return destBuf;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("generate memory tile fmt")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "generate memory tile fmt");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("generate memory tile fmt"));
    }

    [DllImport("ddb", EntryPoint = "DDBSetTag")]
    private static extern DdbResult _SetTag([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newTag);

    public void SetTag(string ddbPath, string newTag)
    {
        if (ddbPath == null)
            throw new ArgumentException("DDB path is null");

        if (newTag == null)
            throw new ArgumentException("New tag is null");

        DdbResult result;
        try
        {
            result = _SetTag(ddbPath, newTag);
            if (result == DdbResult.Success) return;
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("set tag")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "set tag");
    }

    [DllImport("ddb", EntryPoint = "DDBGetTag")]
    private static extern DdbResult _GetTag([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath, out IntPtr outTag);

    public string? GetTag(string ddbPath)
    {
        if (ddbPath == null)
            throw new ArgumentException("DDB path is null");

        try
        {
            var result = _GetTag(ddbPath, out var outTag);
            ThrowForFinalResult(result, "get tag");

            var res = MarshalAndFreeUtf8(outTag);

            return res == null || string.IsNullOrWhiteSpace(res) ? null : res;
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException)
        {
            // Keep typed outcomes (e.g. DdbBusyException from the helper) unwrapped.
            throw;
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("get tag")}\", check inner exception for details",
                ex);
        }
    }

    [DllImport("ddb", EntryPoint = "DDBGetStamp")]
    private static extern DdbResult _DDBGetStamp([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath, out IntPtr output);

    public Stamp GetStamp(string ddbPath)
    {
        if (ddbPath == null)
            throw new ArgumentException("DDB path is null");

        DdbResult result;
        try
        {
            result = _DDBGetStamp(ddbPath, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);

                if (json == null)
                    throw new InvalidOperationException("No result from DDBGetStamp call");

                var res = JsonConvert.DeserializeObject<Stamp>(json);

                if (res == null)
                    throw new InvalidOperationException($"Unable to deserialize stamp result: {json}");

                return res;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("get stamp")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "get stamp");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("get stamp"));
    }

    [DllImport("ddb", EntryPoint = "DDBDelta")]
    private static extern DdbResult _Delta([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbSourceStamp,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ddbTargetStamp, out IntPtr output,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string format);

    public Delta Delta(string ddbPath, string ddbTarget)
    {
        return Delta(GetStamp(ddbPath), GetStamp(ddbTarget));
    }

    [DllImport("ddb", EntryPoint = "DDBApplyDelta")]
    private static extern DdbResult _ApplyDelta([MarshalAs(UnmanagedType.LPUTF8Str)] string delta,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sourcePath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath, int mergeStrategy,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceMetaDump, out IntPtr conflicts);

    public List<string> ApplyDelta(Delta delta, string sourcePath, string ddbPath, MergeStrategy mergeStrategy,
        string? sourceMetaDump = null)
    {
        DdbResult result;
        try
        {
            var deltaJson = JsonConvert.SerializeObject(delta);

            result = _ApplyDelta(deltaJson, sourcePath, ddbPath, (int)mergeStrategy, sourceMetaDump ?? "[]",
                                    out var conflictsPtr);
            if (result == DdbResult.Success)
            {
                var conflicts = MarshalAndFreeUtf8(conflictsPtr);

                if (string.IsNullOrWhiteSpace(conflicts))
                    throw new DdbException("Unable get applydelta result");

                var res = JsonConvert.DeserializeObject<List<string>>(conflicts);

                if (res == null)
                    throw new InvalidOperationException($"Unable to deserialize apply delta result: {conflicts}");

                return res;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("apply delta")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "apply delta");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("apply delta"));
    }


    public Delta Delta(Stamp source, Stamp target)
    {
        DdbResult result;
        try
        {
            var sourceJson = JsonConvert.SerializeObject(source);
            var targetJson = JsonConvert.SerializeObject(target);

            result = _Delta(sourceJson, targetJson, out var output, "json");
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);

                if (string.IsNullOrWhiteSpace(json))
                    throw new InvalidOperationException("Unable get delta");

                var res = JsonConvert.DeserializeObject<Delta>(json);

                if (res == null)
                    throw new InvalidOperationException($"Unable to deserialize delta result: {json}");

                return res;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("delta")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "delta");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("delta"));
    }


    [DllImport("ddb", EntryPoint = "DDBComputeDeltaLocals")]
    private static extern DdbResult _ComputeDeltaLocals([MarshalAs(UnmanagedType.LPUTF8Str)] string delta,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath, [MarshalAs(UnmanagedType.LPUTF8Str)] string hlDestFolder,
        out IntPtr output);

    public Dictionary<string, bool> ComputeDeltaLocals(Delta delta, string ddbPath, string hlDestFolder = "")
    {
        DdbResult result;
        try
        {
            var deltaJson = JsonConvert.SerializeObject(delta);

            result = _ComputeDeltaLocals(deltaJson, ddbPath, hlDestFolder, out var outputPtr);
            if (result == DdbResult.Success)
            {
                var output = MarshalAndFreeUtf8(outputPtr);

                if (string.IsNullOrWhiteSpace(output))
                    throw new DdbException("Unable get ComputeDeltaLocals result");

                var res = JsonConvert.DeserializeObject<Dictionary<string, bool>>(output);

                if (res == null)
                    throw new InvalidOperationException($"Unable to deserialize ComputeDeltaLocals result: {output}");

                return res;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("compute delta locals")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "compute delta locals");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("compute delta locals"));
    }


    [DllImport("ddb", EntryPoint = "DDBMoveEntry")]
    private static extern DdbResult _MoveEntry([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbSource,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string dest);

    public void MoveEntry(string ddbPath, string source, string dest)
    {
        source = source.Replace('\\', '/');
        dest = dest.Replace('\\', '/');
        DdbResult result;
        try
        {
            result = _MoveEntry(ddbPath, source, dest);
            if (result == DdbResult.Success) return;
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("move entry")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "move entry");
    }

    [DllImport("ddb", EntryPoint = "DDBBuild")]
    private static extern DdbResult _Build([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbSource,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? source, [MarshalAs(UnmanagedType.LPUTF8Str)] string? dest, bool force,
        bool pendingOnly);

    public void Build(string ddbPath, string? source = null, string? dest = null, bool force = false,
        bool pendingOnly = false)
    {
        source = source?.Replace('\\', '/');
        dest = dest?.Replace('\\', '/');
        try
        {
            var result = _Build(ddbPath, source, dest, force, pendingOnly);

            // Success: build scheduled/committed. BuildDependencyMissing: a dependency is not
            // indexed yet, so the build is intentionally skipped (legacy silent-return — NOT an
            // error; see the Ddb.BuildDependencyMissing adapter contract), so both return here.
            if (result == DdbResult.Success || result == DdbResult.BuildDependencyMissing)
                return;

            // BuildInProgress (kernel build lock held) and Busy (transient DB contention) surface
            // as typed exceptions via the shared mapper so Hangfire retry decorators
            // (OnlyOn = DdbBusyException / DdbBuildInProgressException) can retry with backoff.
            ThrowForFinalResult(result, "build");
        }
        catch (DdbBuildInProgressException)
        {
            throw;
        }
        catch (DdbBusyException)
        {
            throw;
        }
        catch (DdbException)
        {
            // Keep typed outcomes from the helper unwrapped (it throws DdbException-family types).
            throw;
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("build")}\", check inner exception for details",
                ex);
        }

        // Unreachable (every path returns or throws earlier); kept as a compiler fallback.
        throw new DdbException(SafeGetLastError("build"));
    }

    [DllImport("ddb", EntryPoint = "DDBIsBuildable")]
    private static extern DdbResult _IsBuildable([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbSource,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out bool isBuildable);

    public bool IsBuildable(string ddbPath, string path)
    {
        path = path.Replace('\\', '/');
        DdbResult result;
        try
        {
            result = _IsBuildable(ddbPath, path, out var isBuildable);
            if (result == DdbResult.Success) return isBuildable;
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("is buildable")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "is buildable");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("is buildable"));
    }

    [DllImport("ddb", EntryPoint = "DDBIsBuildActive")]
    private static extern DdbResult _IsBuildActive([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out bool isBuildActive);

    public bool IsBuildActive(string ddbPath, string path)
    {
        path = path.Replace('\\', '/');
        DdbResult result;
        try
        {
            result = _IsBuildActive(ddbPath, path, out var isBuildActive);
            if (result == DdbResult.Success) return isBuildActive;
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("is build active")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "is build active");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("is build active"));
    }

    [DllImport("ddb", EntryPoint = "DDBIsBuildComplete")]
    private static extern DdbResult _IsBuildComplete([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out bool isBuildComplete);

    public bool IsBuildComplete(string ddbPath, string path)
    {
        path = path.Replace('\\', '/');
        DdbResult result;
        try
        {
            result = _IsBuildComplete(ddbPath, path, out var isBuildComplete);
            if (result == DdbResult.Success) return isBuildComplete;
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("is build complete")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "is build complete");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("is build complete"));
    }

    [DllImport("ddb", EntryPoint = "DDBIsBuildPending")]
    private static extern DdbResult _IsBuildPending([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        out bool isBuildPending);

    public bool IsBuildPending(string ddbPath)
    {
        try
        {
            var result = _IsBuildPending(ddbPath, out var isBuildPending);
            ThrowForFinalResult(result, "is build pending");

            return isBuildPending;
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException)
        {
            // Keep typed outcomes (e.g. DdbBusyException from the helper) unwrapped.
            throw;
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("is build pending")}\", check inner exception for details",
                ex);
        }
    }

    [DllImport("ddb", EntryPoint = "DDBGetPendingBuildInfo")]
    private static extern DdbResult _GetPendingBuildInfo([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        out IntPtr output);

    public PendingBuildInfo[] GetPendingBuildInfo(string ddbPath)
    {
        DdbResult result;
        try
        {
            result = _GetPendingBuildInfo(ddbPath, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);

                if (json == null)
                    throw new InvalidOperationException("No result from DDBGetPendingBuildInfo call");

                var res = JsonConvert.DeserializeObject<PendingBuildInfo[]>(json);

                if (res == null)
                    throw new InvalidOperationException($"Unable to deserialize pending build info result: {json}");

                return res;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("get pending build info")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "get pending build info");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("get pending build info"));
    }

    [DllImport("ddb", EntryPoint = "DDBCleanup")]
    private static extern DdbResult _Cleanup([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath, out IntPtr output);

    public DdbCleanupResult Cleanup(string ddbPath)
    {
        DdbResult result;
        try
        {
            result = _Cleanup(ddbPath, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);

                if (json == null)
                    throw new InvalidOperationException("No result from DDBCleanup call");

                var res = JsonConvert.DeserializeObject<DdbCleanupResult>(json);

                if (res == null)
                    throw new InvalidOperationException($"Unable to deserialize cleanup result: {json}");

                return res;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("cleanup")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "cleanup");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("cleanup"));
    }


    [DllImport("ddb", EntryPoint = "DDBMetaAdd")]
    private static extern DdbResult _MetaAdd([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string data, out IntPtr output);

    public Meta MetaAdd(string ddbPath, string key, string data, string? path = null)
    {
        DdbResult result;
        try
        {
            result = _MetaAdd(ddbPath, path ?? string.Empty, key, data, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);

                if (json == null)
                    throw new InvalidOperationException("No result from DDBMetaAdd call");

                var res = JsonConvert.DeserializeObject<Meta>(json);

                if (res == null)
                    throw new InvalidOperationException($"Unable to deserialize meta result: {json}");

                return res;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("meta add")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "meta add");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("meta add"));
    }

    [DllImport("ddb", EntryPoint = "DDBMetaSet")]
    private static extern DdbResult _MetaSet([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string data, out IntPtr output);

    public Meta MetaSet(string ddbPath, string key, string data, string? path = null)
    {
        DdbResult result;
        try
        {
            result = _MetaSet(ddbPath, path ?? string.Empty, key, data, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);

                if (json == null)
                    throw new InvalidOperationException("No result from DDBMetaSet call");

                var res = JsonConvert.DeserializeObject<Meta>(json);

                if (res == null)
                    throw new InvalidOperationException($"Unable to deserialize meta result: {json}");

                return res;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("meta set")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "meta set");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("meta set"));
    }

    [DllImport("ddb", EntryPoint = "DDBMetaRemove")]
    private static extern DdbResult _MetaRemove([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string id, out IntPtr output);

    public int MetaRemove(string ddbPath, string id)
    {
        DdbResult result;
        try
        {
            result = _MetaRemove(ddbPath, id, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);

                if (json == null)
                    throw new InvalidOperationException("No result from DDBMetaRemove call");

                var obj = JsonConvert.DeserializeObject<JObject>(json);

                if (obj == null || !obj.ContainsKey("removed"))
                    throw new InvalidOperationException($"Expected 'removed' field but got '{json}'");

                // ReSharper disable once PossibleNullReferenceException
                return obj["removed"]!.ToObject<int>();
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("meta remove")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "meta remove");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("meta remove"));
    }

    [DllImport("ddb", EntryPoint = "DDBMetaGet")]
    private static extern DdbResult _MetaGet([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key, out IntPtr output);

    public string? MetaGet(string ddbPath, string key, string? path = null)
    {
        DdbResult result;
        try
        {
            result = _MetaGet(ddbPath, path ?? string.Empty, key, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);

                return json;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("meta get")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "meta get");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("meta get"));
    }

    [DllImport("ddb", EntryPoint = "DDBMetaUnset")]
    private static extern DdbResult _MetaUnset([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key, out IntPtr output);

    public int MetaUnset(string ddbPath, string key, string? path = null)
    {
        DdbResult result;
        try
        {
            result = _MetaUnset(ddbPath, path ?? string.Empty, key, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);

                if (json == null)
                    throw new InvalidOperationException("No result from DDBMetaUnset call");

                var obj = JsonConvert.DeserializeObject<JObject>(json);

                if (obj == null || !obj.ContainsKey("removed"))
                    throw new InvalidOperationException($"Expected 'removed' field but got '{json}'");

                // ReSharper disable once PossibleNullReferenceException
                return obj["removed"]!.ToObject<int>();
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("meta unset")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "meta unset");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("meta unset"));
    }


    [DllImport("ddb", EntryPoint = "DDBMetaList")]
    private static extern DdbResult _MetaList([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr output);

    public List<MetaListItem> MetaList(string ddbPath, string? path = null)
    {
        DdbResult result;
        try
        {
            result = _MetaList(ddbPath, path ?? string.Empty, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);

                if (json == null)
                    throw new InvalidOperationException("No result from DDBMetaList call");

                var res = JsonConvert.DeserializeObject<List<MetaListItem>>(json);

                if (res == null)
                    throw new InvalidOperationException($"Unable to deserialize meta list result: {json}");

                return res;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("meta list")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "meta list");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("meta list"));
    }

    [DllImport("ddb", EntryPoint = "DDBMetaDump")]
    private static extern DdbResult _MetaDump([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ids, out IntPtr output);

    public List<MetaDump> MetaDump(string ddbPath, string? ids = null)
    {
        DdbResult result;
        try
        {
            result = _MetaDump(ddbPath, ids ?? "[]", out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);

                if (json == null)
                    throw new InvalidOperationException("No result from DDBMetaDump call");

                var res = JsonConvert.DeserializeObject<List<MetaDump>>(json);

                if (res == null)
                    throw new InvalidOperationException($"Unable to deserialize meta dump result: {json}");

                return res;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("meta dump")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "meta dump");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("meta dump"));
    }

    [DllImport("ddb", EntryPoint = "DDBStac")]
    private static extern DdbResult _Stac([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? entry,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string stacCollectionRoot, [MarshalAs(UnmanagedType.LPUTF8Str)] string id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string stacCatalogRoot, out IntPtr output);

    public JToken Stac(string ddbPath, string? entry, string stacCollectionRoot, string id,
        string stacCatalogRoot)
    {
        DdbResult result;
        try
        {
            result = _Stac(ddbPath, entry ?? string.Empty, stacCollectionRoot, id, stacCatalogRoot, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);

                if (json == null)
                    throw new InvalidOperationException("No result from DDBStac call");

                var res = JsonConvert.DeserializeObject<JToken>(json);

                if (res == null)
                    throw new InvalidOperationException($"Unable to deserialize stac result: {json}");

                return res;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError()}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "stac");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("stac"));
    }

    [DllImport("ddb", EntryPoint = "DDBStacItemCollection")]
    private static extern DdbResult _StacItemCollection(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string stacCollectionRoot,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string stacCatalogRoot,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? bbox,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? datetime,
        int limit, int offset, out IntPtr output);

    public JToken StacItemCollection(string ddbPath, string stacCollectionRoot, string id,
        string stacCatalogRoot, string? bbox, string? datetime, int limit, int offset)
    {
        DdbResult result;
        try
        {
            result = _StacItemCollection(ddbPath, stacCollectionRoot, id, stacCatalogRoot,
                                    bbox ?? string.Empty, datetime ?? string.Empty, limit, offset, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);

                if (json == null)
                    throw new InvalidOperationException("No result from DDBStacItemCollection call");

                var res = JsonConvert.DeserializeObject<JToken>(json);

                if (res == null)
                    throw new InvalidOperationException($"Unable to deserialize stac item collection result: {json}");

                return res;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError()}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "stac item collection");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("stac item collection"));
    }

    [DllImport("ddb", EntryPoint = "DDBRescan")]
    private static extern DdbResult _Rescan(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        out IntPtr output,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string types,
        bool stopOnError);

    public List<RescanResult> RescanIndex(string ddbPath, string? types = null, bool stopOnError = true)
    {
        DdbResult result;
        try
        {
            result = _Rescan(ddbPath, out var output, types ?? string.Empty, stopOnError);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);

                if (string.IsNullOrWhiteSpace(json))
                    throw new DdbException("Unable to get rescan results");

                var res = JsonConvert.DeserializeObject<List<RescanResult>>(json);

                if (res == null)
                    throw new InvalidOperationException($"Unable to deserialize rescan result: {json}");

                return res;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("rescan")}\", check inner exception for details",
                ex);
        }

        ThrowForFinalResult(result, "rescan");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("rescan"));
    }

    #region Multispectral P/Invoke

    [DllImport("ddb", EntryPoint = "DDBGetRasterInfo")]
    private static extern DdbResult _GetRasterInfo(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr output);

    public string GetRasterInfo(string path)
    {
        if (path == null) throw new ArgumentException("path is null");

        DdbResult result;
        try
        {
            result = _GetRasterInfo(path, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);
                if (string.IsNullOrWhiteSpace(json))
                    throw new DdbException("Unable to get raster info");
                return json;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("get raster info")}\", check inner exception for details", ex);
        }

        ThrowForFinalResult(result, "get raster info");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("get raster info"));
    }

    [DllImport("ddb", EntryPoint = "DDBGetRasterMetadata")]
    private static extern DdbResult _GetRasterMetadata(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? formula,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? bandFilter,
        out IntPtr output);

    public string GetRasterMetadata(string path, string? formula = null, string? bandFilter = null)
    {
        if (path == null) throw new ArgumentException("path is null");

        DdbResult result;
        try
        {
            result = _GetRasterMetadata(path, formula, bandFilter, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);
                if (string.IsNullOrWhiteSpace(json))
                    throw new DdbException("Unable to get raster metadata");
                return json;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("get raster metadata")}\", check inner exception for details", ex);
        }

        ThrowForFinalResult(result, "get raster metadata");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("get raster metadata"));
    }

    [DllImport("ddb", EntryPoint = "DDBGenerateMemoryThumbnailEx")]
    private static extern DdbResult _GenerateMemoryThumbnailEx(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filePath, int size,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? preset,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? bands,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? formula,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? bandFilter,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? colormap,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? rescale,
        out IntPtr outBuffer, out int outBufferSize);

    public byte[] GenerateThumbnailEx(string filePath, int size, string? preset = null,
        string? bands = null, string? formula = null, string? bandFilter = null,
        string? colormap = null, string? rescale = null)
    {
        if (filePath == null) throw new ArgumentException("filePath is null");
        if (size <= 0) throw new ArgumentException("size must be positive");

        if (!File.Exists(filePath))
            throw new DdbException($"File not found: '{filePath}'. Cannot generate thumbnail for non-existent file.");

        DdbResult result;
        try
        {
            result = _GenerateMemoryThumbnailEx(filePath, size, preset, bands, formula, bandFilter,
                    colormap, rescale, out var outBuffer, out var outBufferSize);
            if (result == DdbResult.Success)
            {
                var destBuf = new byte[outBufferSize];
                Marshal.Copy(outBuffer, destBuf, 0, outBufferSize);
                _DDBVSIFree(outBuffer);
                return destBuf;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("generate memory thumbnail ex")}\", check inner exception for details", ex);
        }

        throw new DdbException($"{SafeGetLastError("generate memory thumbnail ex")} (file: '{filePath}', size: {size})");
    }

    [DllImport("ddb", EntryPoint = "DDBMemoryTileEx")]
    private static extern DdbResult _GenerateMemoryTileEx(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string inputPath,
        int tz, int tx, int ty,
        int tileSize, bool tms, bool forceRecreate,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string inputPathHash,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? preset,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? bands,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? formula,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? bandFilter,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? colormap,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? rescale,
        out IntPtr outBuffer, out int outBufferSize);

    public byte[] GenerateMemoryTileEx(string inputPath, int tz, int tx, int ty,
        int tileSize, bool tms, bool forceRecreate, string inputPathHash,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null)
    {
        if (inputPath == null) throw new ArgumentException("inputPath is null");

        DdbResult result;
        try
        {
            result = _GenerateMemoryTileEx(inputPath, tz, tx, ty, tileSize, tms, forceRecreate,
                                    inputPathHash, preset, bands, formula, bandFilter, colormap, rescale,
                                    out var outBuffer, out var outBufferSize);
            if (result == DdbResult.Success)
            {
                var destBuf = new byte[outBufferSize];
                Marshal.Copy(outBuffer, destBuf, 0, outBufferSize);
                _DDBVSIFree(outBuffer);
                return destBuf;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("generate memory tile ex")}\", check inner exception for details", ex);
        }

        ThrowForFinalResult(result, "generate memory tile ex");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("generate memory tile ex"));
    }

    [DllImport("ddb", EntryPoint = "DDBValidateMergeMultispectral")]
    private static extern DdbResult _ValidateMergeMultispectral(
        IntPtr[] paths, int numPaths, out IntPtr output);

    public string ValidateMergeMultispectral(string[] paths)
    {
        if (paths == null || paths.Length == 0)
            throw new ArgumentException("paths is null or empty");

        var utf8Ptrs = MarshalStringArrayToUtf8(paths);
        DdbResult result;
        try
        {
            result = _ValidateMergeMultispectral(utf8Ptrs, paths.Length, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);
                if (string.IsNullOrWhiteSpace(json))
                    throw new DdbException("Unable to validate merge multispectral");
                return json;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("validate merge multispectral")}\", check inner exception for details", ex);
        }
        finally
        {
            FreeUtf8StringArray(utf8Ptrs);
        }

        ThrowForFinalResult(result, "validate merge multispectral");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("validate merge multispectral"));
    }

    [DllImport("ddb", EntryPoint = "DDBPreviewMergeMultispectral")]
    private static extern DdbResult _PreviewMergeMultispectral(
        IntPtr[] paths, int numPaths,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? previewBands,
        int thumbSize,
        out IntPtr outBuffer, out int outBufferSize);

    public byte[] PreviewMergeMultispectral(string[] paths, string? previewBands = null, int thumbSize = 512)
    {
        if (paths == null || paths.Length == 0)
            throw new ArgumentException("paths is null or empty");

        var utf8Ptrs = MarshalStringArrayToUtf8(paths);
        DdbResult result;
        try
        {
            result = _PreviewMergeMultispectral(utf8Ptrs, paths.Length, previewBands, thumbSize,
                                    out var outBuffer, out var outBufferSize);
            if (result == DdbResult.Success)
            {
                var destBuf = new byte[outBufferSize];
                Marshal.Copy(outBuffer, destBuf, 0, outBufferSize);
                _DDBVSIFree(outBuffer);
                return destBuf;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("preview merge multispectral")}\", check inner exception for details", ex);
        }
        finally
        {
            FreeUtf8StringArray(utf8Ptrs);
        }

        ThrowForFinalResult(result, "preview merge multispectral");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("preview merge multispectral"));
    }

    [DllImport("ddb", EntryPoint = "DDBMergeMultispectral")]
    private static extern DdbResult _MergeMultispectral(
        IntPtr[] paths, int numPaths,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string outputCog);

    public void MergeMultispectral(string[] paths, string outputCog)
    {
        if (paths == null || paths.Length == 0)
            throw new ArgumentException("paths is null or empty");
        if (string.IsNullOrWhiteSpace(outputCog))
            throw new ArgumentException("outputCog is null or empty");

        var utf8Ptrs = MarshalStringArrayToUtf8(paths);
        DdbResult result;
        try
        {
            result = _MergeMultispectral(utf8Ptrs, paths.Length, outputCog);
            if (result == DdbResult.Success) return;
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("merge multispectral")}\", check inner exception for details", ex);
        }
        finally
        {
            FreeUtf8StringArray(utf8Ptrs);
        }

        ThrowForFinalResult(result, "merge multispectral");
    }

    [DllImport("ddb", EntryPoint = "DDBValidateAlignRaster")]
    private static extern DdbResult _ValidateAlignRaster(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sourcePath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string referencePath,
        out IntPtr output);

    public string ValidateAlignRaster(string sourcePath, string referencePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("sourcePath is null or empty");
        if (string.IsNullOrWhiteSpace(referencePath))
            throw new ArgumentException("referencePath is null or empty");

        DdbResult result;
        try
        {
            result = _ValidateAlignRaster(sourcePath, referencePath, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);
                if (string.IsNullOrWhiteSpace(json))
                    throw new DdbException("Unable to validate align raster");
                return json;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("validate align raster")}\", check inner exception for details", ex);
        }

        ThrowForFinalResult(result, "validate align raster");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("validate align raster"));
    }

    [DllImport("ddb", EntryPoint = "DDBAlignRaster")]
    private static extern DdbResult _AlignRaster(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sourcePath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string referencePath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string outputPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? mode,
        out IntPtr output);

    public string AlignRaster(string sourcePath, string referencePath, string outputPath, string mode = "similarity")
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("sourcePath is null or empty");
        if (string.IsNullOrWhiteSpace(referencePath))
            throw new ArgumentException("referencePath is null or empty");
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("outputPath is null or empty");

        DdbResult result;
        try
        {
            result = _AlignRaster(sourcePath, referencePath, outputPath, mode, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);
                if (string.IsNullOrWhiteSpace(json))
                    throw new DdbException("Unable to align raster");
                return json;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("align raster")}\", check inner exception for details", ex);
        }

        ThrowForFinalResult(result, "align raster");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("align raster"));
    }

    [DllImport("ddb", EntryPoint = "DDBExportRaster")]
    private static extern DdbResult _ExportRaster(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string inputPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string outputPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? preset,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? bands,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? formula,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? bandFilter,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? colormap,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? rescale);

    public void ExportRaster(string inputPath, string outputPath,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException("inputPath is null or empty");
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("outputPath is null or empty");

        DdbResult result;
        try
        {
            result = _ExportRaster(inputPath, outputPath, preset, bands, formula, bandFilter,
                                    colormap, rescale);
            if (result == DdbResult.Success)
                return;
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("export raster")}\", check inner exception for details", ex);
        }

        ThrowForFinalResult(result, "export raster");
    }

    // Native progress callback contract mirroring DDBProgressCallback in ddb.h:
    //   int (*)(double fraction, const char *phase, void *userData)
    // Returning a non-zero value requests cooperative cancellation.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DdbProgressCallbackNative(double fraction, IntPtr phase, IntPtr userData);

    [DllImport("ddb", EntryPoint = "DDBExportRaster2")]
    private static extern DdbResult _ExportRaster2(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string inputPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string outputPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? preset,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? bands,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? formula,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? bandFilter,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? colormap,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? rescale,
        int tileSize,
        DdbProgressCallbackNative? progress,
        IntPtr progressUserData);

    /// <summary>
    /// Export raster with visualization params applied as GeoTIFF using the
    /// block-windowed implementation (bounded peak memory). Supports incremental
    /// progress reporting and cooperative cancellation.
    /// </summary>
    /// <param name="tileSize">Tile size in pixels for windowed processing; 0 = auto.</param>
    /// <param name="progress">Optional callback invoked with (fraction 0..1, phase). May be null.</param>
    /// <param name="cancellationToken">Cancels the operation cooperatively; raises <see cref="DdbCanceledException"/>.</param>
    public void ExportRaster(string inputPath, string outputPath,
        string? preset, string? bands, string? formula, string? bandFilter,
        string? colormap, string? rescale,
        int tileSize, Action<double, string?>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException("inputPath is null or empty");
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("outputPath is null or empty");

        // Bridge managed progress + cancellation to the native callback contract.
        // We only allocate a native delegate when there is something to observe.
        DdbProgressCallbackNative? nativeCallback = null;
        if (progress != null || cancellationToken.CanBeCanceled)
        {
            nativeCallback = (fraction, phasePtr, _) =>
            {
                if (cancellationToken.IsCancellationRequested)
                    return 1; // request cancellation

                if (progress != null)
                {
                    var phase = phasePtr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(phasePtr);
                    progress(fraction, phase);
                }

                return 0; // continue
            };
        }

        try
        {
            var result = _ExportRaster2(inputPath, outputPath, preset, bands, formula,
                bandFilter, colormap, rescale, tileSize, nativeCallback, IntPtr.Zero);

            // Canceled → DdbCanceledException, all other non-success codes → typed exceptions
            // (see ThrowForFinalResult); the explicit rethrow below keeps the canceled type
            // from being re-wrapped by the generic catch.
            ThrowForFinalResult(result, "export raster");
        }
        catch (DdbCanceledException)
        {
            throw;
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("export raster")}\", check inner exception for details", ex);
        }
        finally
        {
            // Keep the delegate alive across the synchronous P/Invoke.
            GC.KeepAlive(nativeCallback);
        }
    }

    #endregion

    #region Raster Analysis P/Invoke

    [DllImport("ddb", EntryPoint = "DDBGetRasterValueInfo")]
    private static extern DdbResult _GetRasterValueInfo(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr output);

    public string GetRasterValueInfo(string path)
    {
        if (path == null) throw new ArgumentException("path is null");

        DdbResult result;
        try
        {
            result = _GetRasterValueInfo(path, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);
                if (string.IsNullOrWhiteSpace(json))
                    throw new DdbException("Unable to get raster value info");
                return json;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("get raster value info")}\", check inner exception for details", ex);
        }

        ThrowForFinalResult(result, "get raster value info");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("get raster value info"));
    }

    [DllImport("ddb", EntryPoint = "DDBGetRasterPointValue")]
    private static extern DdbResult _GetRasterPointValue(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int x, int y, out IntPtr output);

    public string GetRasterPointValue(string path, int x, int y)
    {
        if (path == null) throw new ArgumentException("path is null");

        DdbResult result;
        try
        {
            result = _GetRasterPointValue(path, x, y, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);
                if (string.IsNullOrWhiteSpace(json))
                    throw new DdbException("Unable to get raster point value");
                return json;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("get raster point value")}\", check inner exception for details", ex);
        }

        ThrowForFinalResult(result, "get raster point value");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("get raster point value"));
    }

    [DllImport("ddb", EntryPoint = "DDBGetRasterAreaStats")]
    private static extern DdbResult _GetRasterAreaStats(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int x0, int y0, int x1, int y1, out IntPtr output);

    public string GetRasterAreaStats(string path, int x0, int y0, int x1, int y1)
    {
        if (path == null) throw new ArgumentException("path is null");

        DdbResult result;
        try
        {
            result = _GetRasterAreaStats(path, x0, y0, x1, y1, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);
                if (string.IsNullOrWhiteSpace(json))
                    throw new DdbException("Unable to get raster area stats");
                return json;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("get raster area stats")}\", check inner exception for details", ex);
        }

        ThrowForFinalResult(result, "get raster area stats");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("get raster area stats"));
    }

    [DllImport("ddb", EntryPoint = "DDBGetRasterProfile")]
    private static extern DdbResult _GetRasterProfile(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string geoJsonLineString,
        int samples,
        out IntPtr output);

    public string GetRasterProfile(string path, string geoJsonLineString, int samples)
    {
        if (path == null) throw new ArgumentException("path is null");
        if (geoJsonLineString == null) throw new ArgumentException("geoJsonLineString is null");

        DdbResult result;
        try
        {
            result = _GetRasterProfile(path, geoJsonLineString, samples, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);
                if (string.IsNullOrWhiteSpace(json))
                    throw new DdbException("Unable to get raster profile");
                return json;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("get raster profile")}\", check inner exception for details", ex);
        }

        ThrowForFinalResult(result, "get raster profile");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("get raster profile"));
    }

    [DllImport("ddb", EntryPoint = "DDBCalculateVolume")]
    private static extern DdbResult _CalculateVolume(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string rasterPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string polygonGeoJson,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string baseMethod,
        double flatElevation,
        out IntPtr output);

    public string CalculateVolume(string path, string polygonGeoJson, string baseMethod, double flatElevation)
    {
        if (path == null) throw new ArgumentException("path is null");
        if (polygonGeoJson == null) throw new ArgumentException("polygonGeoJson is null");
        baseMethod ??= string.Empty;

        DdbResult result;
        try
        {
            result = _CalculateVolume(path, polygonGeoJson, baseMethod, flatElevation, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);
                if (string.IsNullOrWhiteSpace(json))
                    throw new DdbException("Unable to calculate volume");
                return json;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("calculate volume")}\", check inner exception for details", ex);
        }

        ThrowForFinalResult(result, "calculate volume");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("calculate volume"));
    }

    [DllImport("ddb", EntryPoint = "DDBDetectStockpile")]
    private static extern DdbResult _DetectStockpile(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string rasterPath,
        double lat,
        double lon,
        double radius,
        float sensitivity,
        out IntPtr output);

    public string DetectStockpile(string path, double lat, double lon, double radiusMeters, float sensitivity)
    {
        if (path == null) throw new ArgumentException("path is null");
        if (!(radiusMeters > 0)) throw new ArgumentException("radius must be positive", nameof(radiusMeters));

        DdbResult result;
        try
        {
            result = _DetectStockpile(path, lat, lon, radiusMeters, sensitivity, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);
                if (string.IsNullOrWhiteSpace(json))
                    throw new DdbException("Unable to detect stockpile");
                return json;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("detect stockpile")}\", check inner exception for details", ex);
        }

        ThrowForFinalResult(result, "detect stockpile");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("detect stockpile"));
    }

    [DllImport("ddb", EntryPoint = "DDBDetectAllStockpiles")]
    private static extern DdbResult _DetectAllStockpiles(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string rasterPath,
        float sensitivity,
        double minAreaM2,
        int maxResults,
        out IntPtr output);

    public string DetectAllStockpiles(string path, float sensitivity, double minAreaM2, int maxResults)
    {
        if (path == null) throw new ArgumentException("path is null");
        if (sensitivity < 0f || sensitivity > 1f) throw new ArgumentException("sensitivity must be in [0,1]", nameof(sensitivity));
        if (minAreaM2 < 0.0) throw new ArgumentException("minAreaM2 must be >= 0", nameof(minAreaM2));
        if (maxResults <= 0) throw new ArgumentException("maxResults must be > 0", nameof(maxResults));

        DdbResult result;
        try
        {
            result = _DetectAllStockpiles(path, sensitivity, minAreaM2, maxResults, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);
                if (string.IsNullOrWhiteSpace(json))
                    throw new DdbException("Unable to detect stockpiles");
                return json;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("detect all stockpiles")}\", check inner exception for details", ex);
        }

        ThrowForFinalResult(result, "detect all stockpiles");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("detect all stockpiles"));
    }

    [DllImport("ddb", EntryPoint = "DDBGenerateContours")]
    private static extern DdbResult _GenerateContours(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string rasterPath,
        double interval,
        int count,
        double baseOffset,
        double minElev,
        double maxElev,
        double simplifyTolerance,
        int bandIndex,
        out IntPtr output);

    /// <summary>
    /// Generate contour lines (GeoJSON FeatureCollection of LineStrings with
    /// an `elev` property) from a single-band elevation raster.
    /// </summary>
    /// <param name="path">Path to the raster (DEM/DSM/DTM).</param>
    /// <param name="interval">Contour interval (raster units). When null, <paramref name="count"/> drives the spacing.</param>
    /// <param name="count">Target number of contour levels. Used when <paramref name="interval"/> is null.</param>
    /// <param name="baseOffset">Reference base elevation for level alignment.</param>
    /// <param name="minElev">Drop contours below this elevation. Null disables the bound.</param>
    /// <param name="maxElev">Drop contours above this elevation. Null disables the bound.</param>
    /// <param name="simplifyTolerance">Geometry simplification tolerance in raster CRS units (0 = none).</param>
    /// <param name="bandIndex">1-based raster band index (defaults to 1).</param>
    public string GenerateContours(string path,
                                   double? interval,
                                   int? count,
                                   double baseOffset = 0.0,
                                   double? minElev = null,
                                   double? maxElev = null,
                                   double simplifyTolerance = 0.0,
                                   int bandIndex = 1)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("path is null or empty", nameof(path));
        if (!interval.HasValue && !count.HasValue)
            throw new ArgumentException("Either interval or count must be specified");
        if (interval.HasValue && interval.Value <= 0.0)
            throw new ArgumentException("interval must be > 0", nameof(interval));
        if (count.HasValue && count.Value <= 0)
            throw new ArgumentException("count must be > 0", nameof(count));
        if (simplifyTolerance < 0.0)
            throw new ArgumentException("simplifyTolerance must be >= 0", nameof(simplifyTolerance));
        if (bandIndex <= 0)
            throw new ArgumentException("bandIndex must be > 0", nameof(bandIndex));

        var iv = interval ?? 0.0;        // <= 0 => unset on native side
        var cnt = count ?? 0;            // <= 0 => unset on native side
        var lo = minElev ?? double.NaN;  // NaN => unset on native side
        var hi = maxElev ?? double.NaN;  // NaN => unset on native side

        DdbResult result;
        try
        {
            result = _GenerateContours(path, iv, cnt, baseOffset, lo, hi,
                                                  simplifyTolerance, bandIndex, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);
                if (string.IsNullOrWhiteSpace(json))
                    throw new DdbException("Unable to generate contours");
                return json;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("generate contours")}\", check inner exception for details", ex);
        }

        ThrowForFinalResult(result, "generate contours");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("generate contours"));
    }

    #endregion

    [DllImport("ddb", EntryPoint = "DDBMaskBorders")]
    private static extern DdbResult _MaskBorders(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string input,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string output,
        int nearDist,
        [MarshalAs(UnmanagedType.Bool)] bool white);

    public void MaskBorders(string input, string output, int nearDist = 15, bool white = false)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("input is null or empty");
        if (string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("output is null or empty");

        DdbResult result;
        try
        {
            result = _MaskBorders(input, output, nearDist, white);
            if (result == DdbResult.Success) return;
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("mask borders")}\", check inner exception for details", ex);
        }

        ThrowForFinalResult(result, "mask borders");
    }

    #region OGC services (raster region + vector query/describe) P/Invoke

    [DllImport("ddb", EntryPoint = "DDBRenderRasterRegionEx")]
    private static extern DdbResult _RenderRasterRegionEx(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string inputPath,
        [In] double[] bbox,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string bboxSrs,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string outputCrs,
        [In] int[] bands,
        int bandCount,
        int width, int height,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string format,
        out IntPtr outBuffer, out int outBufferSize);

    public byte[] RenderRasterRegion(string inputPath, double[] bbox, string bboxSrs,
                                     int width, int height, string format,
                                     int[]? bands = null, string? outputCrs = null)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException("inputPath is null or empty");
        if (bbox == null || bbox.Length != 4)
            throw new ArgumentException("bbox must contain exactly 4 elements: [minX,minY,maxX,maxY]");
        if (width <= 0 || height <= 0)
            throw new ArgumentException("width and height must be positive");
        if (string.IsNullOrWhiteSpace(format))
            throw new ArgumentException("format is null or empty");

        // Always go through the Ex entry point so both legacy and OGC WCS
        // (band subset + alternate output CRS) callers share the same path.
        var bandArr = bands ?? [];

        DdbResult result;
        try
        {
            result = _RenderRasterRegionEx(inputPath, bbox, bboxSrs ?? string.Empty,
                                                      outputCrs ?? string.Empty,
                                                      bandArr, bandArr.Length,
                                                      width, height, format,
                                                      out var outBuffer, out var outSize);
            if (result == DdbResult.Success)
            {
                var dest = new byte[outSize];
                Marshal.Copy(outBuffer, dest, 0, outSize);
                _DDBVSIFree(outBuffer);
                return dest;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("render raster region")}\", check inner exception for details", ex);
        }

        ThrowForFinalResult(result, "render raster region");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("render raster region"));
    }

    [DllImport("ddb", EntryPoint = "DDBRenderRasterIndex")]
    private static extern DdbResult _RenderRasterIndex(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string inputPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string indexName,
        [In] double[] bbox,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string bboxSrs,
        int width, int height,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string format,
        out IntPtr outBuffer, out int outBufferSize);

    public byte[] RenderRasterIndex(string inputPath, string indexName, double[] bbox,
                                    string bboxSrs, int width, int height, string format)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException("inputPath is null or empty");
        if (string.IsNullOrWhiteSpace(indexName))
            throw new ArgumentException("indexName is null or empty");
        if (bbox == null || bbox.Length != 4)
            throw new ArgumentException("bbox must contain exactly 4 elements: [minX,minY,maxX,maxY]");
        if (width <= 0 || height <= 0)
            throw new ArgumentException("width and height must be positive");
        if (string.IsNullOrWhiteSpace(format))
            throw new ArgumentException("format is null or empty");

        DdbResult result;
        try
        {
            result = _RenderRasterIndex(inputPath, indexName, bbox, bboxSrs ?? string.Empty,
                                                   width, height, format,
                                                   out var outBuffer, out var outSize);
            if (result == DdbResult.Success)
            {
                var dest = new byte[outSize];
                Marshal.Copy(outBuffer, dest, 0, outSize);
                _DDBVSIFree(outBuffer);
                return dest;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("render raster index")}\", check inner exception for details", ex);
        }

        ThrowForFinalResult(result, "render raster index");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("render raster index"));
    }

    [DllImport("ddb", EntryPoint = "DDBQueryRasterPoint")]
    private static extern DdbResult _QueryRasterPoint(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string inputPath,
        double x, double y,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string srs,
        out IntPtr output);

    public string QueryRasterPoint(string inputPath, double x, double y, string? srs = null)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException("inputPath is null or empty");

        DdbResult result;
        try
        {
            result = _QueryRasterPoint(inputPath, x, y, srs ?? string.Empty, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);
                if (string.IsNullOrWhiteSpace(json))
                    throw new DdbException("Unable to query raster point");
                return json;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("query raster point")}\", check inner exception for details", ex);
        }

        ThrowForFinalResult(result, "query raster point");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("query raster point"));
    }

    [DllImport("ddb", EntryPoint = "DDBQueryVector")]
    private static extern DdbResult _QueryVector(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string vectorPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? layerName,
        [In] double[]? bbox,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? bboxSrs,
        int maxFeatures, int startIndex,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string outputFormat,
        out IntPtr output);

    public string QueryVector(string vectorPath, string? layerName = null,
                              double[]? bbox = null, string? bboxSrs = null,
                              int maxFeatures = 1000, int startIndex = 0,
                              string outputFormat = "application/json")
    {
        if (string.IsNullOrWhiteSpace(vectorPath))
            throw new ArgumentException("vectorPath is null or empty");
        if (bbox != null && bbox.Length != 4)
            throw new ArgumentException("bbox must contain exactly 4 elements when provided");

        DdbResult result;
        try
        {
            result = _QueryVector(vectorPath, layerName, bbox, bboxSrs,
                                             maxFeatures, startIndex,
                                             outputFormat ?? "application/json",
                                             out var output);
            if (result == DdbResult.Success)
            {
                var data = MarshalAndFreeUtf8(output);
                if (string.IsNullOrWhiteSpace(data))
                    throw new DdbException("Unable to query vector");
                return data;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("query vector")}\", check inner exception for details", ex);
        }

        ThrowForFinalResult(result, "query vector");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("query vector"));
    }

    [DllImport("ddb", EntryPoint = "DDBDescribeVector")]
    private static extern DdbResult _DescribeVector(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string vectorPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? layerName,
        out IntPtr output);

    public string DescribeVector(string vectorPath, string? layerName = null)
    {
        if (string.IsNullOrWhiteSpace(vectorPath))
            throw new ArgumentException("vectorPath is null or empty");

        DdbResult result;
        try
        {
            result = _DescribeVector(vectorPath, layerName, out var output);
            if (result == DdbResult.Success)
            {
                var json = MarshalAndFreeUtf8(output);
                if (string.IsNullOrWhiteSpace(json))
                    throw new DdbException("Unable to describe vector");
                return json;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new DdbException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
        }
        catch (DdbException) { throw; }
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("describe vector")}\", check inner exception for details", ex);
        }

        ThrowForFinalResult(result, "describe vector");

        // Unreachable in practice (Success bails above); kept for exhaustiveness.
        throw new DdbException(SafeGetLastError("describe vector"));
    }

    #endregion
}
