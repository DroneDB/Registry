#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Ports;
using Registry.Ports.DroneDB;

namespace Registry.Adapters.DroneDB;

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

    [DllImport("ddb", EntryPoint = "DDBGetLastError")]
    private static extern IntPtr _GetLastError();

    private static string? GetLastError()
    {
        var ptr = _GetLastError();
        return Marshal.PtrToStringUTF8(ptr);
    }

    private static string SafeGetLastError(string? operation = null)
    {
        return GetLastError() ?? (operation != null ? "Unknown error in " + operation : "Unknown error");
    }

    [DllImport("ddb", EntryPoint = "DDBInit")]
    private static extern DdbResult _Init([MarshalAs(UnmanagedType.LPUTF8Str)] string directory, out IntPtr outPath);

    public string Init(string directory)
    {
        try
        {
            if (_Init(directory, out var outPath) == DdbResult.Success)
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
        var utf8Ptrs = MarshalStringArrayToUtf8(paths);
        try
        {
            if (_Add(ddbPath, utf8Ptrs, paths.Length, out var output, recursive) == DdbResult.Success)
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

        throw new DdbException(SafeGetLastError("add"));
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
        var utf8Ptrs = MarshalStringArrayToUtf8(paths);
        try
        {
            if (_Remove(ddbPath, utf8Ptrs, paths?.Length ?? 0) == DdbResult.Success) return;
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

        throw new DdbException(SafeGetLastError("remove"));
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
        try
        {
            if (_Info(utf8Ptrs, paths?.Length ?? 0, out var output, "json", recursive, maxRecursionDepth, "auto",
                    withHash) ==
                DdbResult.Success)
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
        try
        {
            var lst = _List(ddbPath, utf8Ptrs, paths.Length, out var output, "json", recursive, maxRecursionDepth);

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

        throw new DdbException(SafeGetLastError("list"));
    }

    [DllImport("ddb", EntryPoint = "DDBAppendPassword")]
    private static extern DdbResult _AppendPassword(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password);

    public void AppendPassword(string ddbPath, string password)
    {
        try
        {
            if (_AppendPassword(ddbPath, password) == DdbResult.Success) return;
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

        throw new DdbException(SafeGetLastError("append password"));
    }

    [DllImport("ddb", EntryPoint = "DDBVerifyPassword")]
    private static extern DdbResult _VerifyPassword(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
        out bool verified);

    public bool VerifyPassword(string ddbPath, string password)
    {
        try
        {
            if (_VerifyPassword(ddbPath, password, out var res) ==
                DdbResult.Success) return res;
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

        throw new DdbException(SafeGetLastError("verify password"));
    }

    [DllImport("ddb", EntryPoint = "DDBClearPasswords")]
    private static extern DdbResult _ClearPasswords(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath);

    public void ClearPasswords(string ddbPath)
    {
        try
        {
            if (_ClearPasswords(ddbPath) == DdbResult.Success) return;
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

        throw new DdbException(SafeGetLastError("clear passwords"));
    }

    [DllImport("ddb", EntryPoint = "DDBChattr")]
    private static extern DdbResult _ChangeAttributes(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath, [MarshalAs(UnmanagedType.LPUTF8Str)] string attributesJson,
        out IntPtr jsonOutput);

    public Dictionary<string, object> ChangeAttributes(string ddbPath, Dictionary<string, object> attributes)
    {
        if (attributes == null)
            throw new ArgumentException("Attributes is null");

        try
        {
            var attrs = JsonConvert.SerializeObject(attributes);

            if (_ChangeAttributes(ddbPath, attrs, out var output) ==
                DdbResult.Success)
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

        try
        {
            if (_GenerateThumbnail(filePath, size, destPath) ==
                DdbResult.Success) return;
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

        try
        {
            if (_GenerateMemoryThumbnail(filePath, size, out var outBuffer, out var outBufferSize) ==
                DdbResult.Success)
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

        try
        {
            if (_GenerateTile(inputPath, tz, tx, ty, out var output, tileSize, tms, forceRecreate) ==
                DdbResult.Success)
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

        try
        {
            if (_GenerateMemoryTile(inputPath, tz, tx, ty, out var outBuffer, out var outBufferSize, tileSize, tms,
                    forceRecreate, inputPathHash) ==
                DdbResult.Success)
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

        throw new DdbException(SafeGetLastError("generate memory tile"));
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

        try
        {
            if (_SetTag(ddbPath, newTag) == DdbResult.Success) return;
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

        throw new DdbException(SafeGetLastError("set tag"));
    }

    [DllImport("ddb", EntryPoint = "DDBGetTag")]
    private static extern DdbResult _GetTag([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath, out IntPtr outTag);

    public string? GetTag(string ddbPath)
    {
        if (ddbPath == null)
            throw new ArgumentException("DDB path is null");

        try
        {
            if (_GetTag(ddbPath, out var outTag) !=
                DdbResult.Success) throw new DdbException(SafeGetLastError());

            var res = MarshalAndFreeUtf8(outTag);

            return res == null || string.IsNullOrWhiteSpace(res) ? null : res;
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
    }

    [DllImport("ddb", EntryPoint = "DDBGetStamp")]
    private static extern DdbResult _DDBGetStamp([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath, out IntPtr output);

    public Stamp GetStamp(string ddbPath)
    {
        if (ddbPath == null)
            throw new ArgumentException("DDB path is null");

        try
        {
            if (_DDBGetStamp(ddbPath, out var output) ==
                DdbResult.Success)
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
        try
        {
            var deltaJson = JsonConvert.SerializeObject(delta);

            if (_ApplyDelta(deltaJson, sourcePath, ddbPath, (int)mergeStrategy, sourceMetaDump ?? "[]",
                    out var conflictsPtr) ==
                DdbResult.Success)
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

        throw new DdbException(SafeGetLastError("apply delta"));
    }


    public Delta Delta(Stamp source, Stamp target)
    {
        try
        {
            var sourceJson = JsonConvert.SerializeObject(source);
            var targetJson = JsonConvert.SerializeObject(target);

            if (_Delta(sourceJson, targetJson, out var output, "json") ==
                DdbResult.Success)
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

        throw new DdbException(SafeGetLastError("delta"));
    }


    [DllImport("ddb", EntryPoint = "DDBComputeDeltaLocals")]
    private static extern DdbResult _ComputeDeltaLocals([MarshalAs(UnmanagedType.LPUTF8Str)] string delta,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath, [MarshalAs(UnmanagedType.LPUTF8Str)] string hlDestFolder,
        out IntPtr output);

    public Dictionary<string, bool> ComputeDeltaLocals(Delta delta, string ddbPath, string hlDestFolder = "")
    {
        try
        {
            var deltaJson = JsonConvert.SerializeObject(delta);

            if (_ComputeDeltaLocals(deltaJson, ddbPath, hlDestFolder, out var outputPtr) ==
                DdbResult.Success)
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

        throw new DdbException(SafeGetLastError("compute delta locals"));
    }


    [DllImport("ddb", EntryPoint = "DDBMoveEntry")]
    private static extern DdbResult _MoveEntry([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbSource,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string dest);

    public void MoveEntry(string ddbPath, string source, string dest)
    {
        try
        {
            if (_MoveEntry(ddbPath, source, dest) == DdbResult.Success) return;
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

        throw new DdbException(SafeGetLastError("move entry"));
    }

    [DllImport("ddb", EntryPoint = "DDBBuild")]
    private static extern DdbResult _Build([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbSource,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? source, [MarshalAs(UnmanagedType.LPUTF8Str)] string? dest, bool force,
        bool pendingOnly);

    public void Build(string ddbPath, string? source = null, string? dest = null, bool force = false,
        bool pendingOnly = false)
    {
        try
        {
            if (_Build(ddbPath, source, dest, force, pendingOnly) != DdbResult.Exception) return;
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

        throw new DdbException(SafeGetLastError("build"));
    }

    [DllImport("ddb", EntryPoint = "DDBIsBuildable")]
    private static extern DdbResult _IsBuildable([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbSource,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out bool isBuildable);

    public bool IsBuildable(string ddbPath, string path)
    {
        try
        {
            if (_IsBuildable(ddbPath, path, out var isBuildable) ==
                DdbResult.Success) return isBuildable;
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

        throw new DdbException(SafeGetLastError("is buildable"));
    }

    [DllImport("ddb", EntryPoint = "DDBIsBuildActive")]
    private static extern DdbResult _IsBuildActive([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out bool isBuildActive);

    public bool IsBuildActive(string ddbPath, string path)
    {
        try
        {
            if (_IsBuildActive(ddbPath, path, out var isBuildActive) ==
                DdbResult.Success) return isBuildActive;
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

        throw new DdbException(SafeGetLastError("is build active"));
    }

    [DllImport("ddb", EntryPoint = "DDBIsBuildPending")]
    private static extern DdbResult _IsBuildPending([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        out bool isBuildPending);

    public bool IsBuildPending(string ddbPath)
    {
        try
        {
            if (_IsBuildPending(ddbPath, out var isBuildPending) !=
                DdbResult.Success) throw new DdbException(SafeGetLastError());

            return isBuildPending;
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
    }

    [DllImport("ddb", EntryPoint = "DDBMetaAdd")]
    private static extern DdbResult _MetaAdd([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string data, out IntPtr output);

    public Meta MetaAdd(string ddbPath, string key, string data, string? path = null)
    {
        try
        {
            if (_MetaAdd(ddbPath, path ?? string.Empty, key, data, out var output) ==
                DdbResult.Success)
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

        throw new DdbException(SafeGetLastError("meta add"));
    }

    [DllImport("ddb", EntryPoint = "DDBMetaSet")]
    private static extern DdbResult _MetaSet([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string data, out IntPtr output);

    public Meta MetaSet(string ddbPath, string key, string data, string? path = null)
    {
        try
        {
            if (_MetaSet(ddbPath, path ?? string.Empty, key, data, out var output) ==
                DdbResult.Success)
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

        throw new DdbException(SafeGetLastError("meta set"));
    }

    [DllImport("ddb", EntryPoint = "DDBMetaRemove")]
    private static extern DdbResult _MetaRemove([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string id, out IntPtr output);

    public int MetaRemove(string ddbPath, string id)
    {
        try
        {
            if (_MetaRemove(ddbPath, id, out var output) ==
                DdbResult.Success)
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

        throw new DdbException(SafeGetLastError("meta remove"));
    }

    [DllImport("ddb", EntryPoint = "DDBMetaGet")]
    private static extern DdbResult _MetaGet([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key, out IntPtr output);

    public string? MetaGet(string ddbPath, string key, string? path = null)
    {
        try
        {
            if (_MetaGet(ddbPath, path ?? string.Empty, key, out var output) ==
                DdbResult.Success)
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

        throw new DdbException(SafeGetLastError("meta get"));
    }

    [DllImport("ddb", EntryPoint = "DDBMetaUnset")]
    private static extern DdbResult _MetaUnset([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key, out IntPtr output);

    public int MetaUnset(string ddbPath, string key, string? path = null)
    {
        try
        {
            if (_MetaUnset(ddbPath, path ?? string.Empty, key, out var output) ==
                DdbResult.Success)
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

        throw new DdbException(SafeGetLastError("meta unset"));
    }


    [DllImport("ddb", EntryPoint = "DDBMetaList")]
    private static extern DdbResult _MetaList([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr output);

    public List<MetaListItem> MetaList(string ddbPath, string? path = null)
    {
        try
        {
            if (_MetaList(ddbPath, path ?? string.Empty, out var output) ==
                DdbResult.Success)
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

        throw new DdbException(SafeGetLastError("meta list"));
    }

    [DllImport("ddb", EntryPoint = "DDBMetaDump")]
    private static extern DdbResult _MetaDump([MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ids, out IntPtr output);

    public List<MetaDump> MetaDump(string ddbPath, string? ids = null)
    {
        try
        {
            if (_MetaDump(ddbPath, ids ?? "[]", out var output) ==
                DdbResult.Success)
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
        try
        {
            if (_Stac(ddbPath, entry ?? string.Empty, stacCollectionRoot, id, stacCatalogRoot, out var output) ==
                DdbResult.Success)
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

        throw new DdbException(SafeGetLastError("stac"));
    }

    [DllImport("ddb", EntryPoint = "DDBRescan")]
    private static extern DdbResult _Rescan(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ddbPath,
        out IntPtr output,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string types,
        bool stopOnError);

    public List<RescanResult> RescanIndex(string ddbPath, string? types = null, bool stopOnError = true)
    {
        try
        {
            if (_Rescan(ddbPath, out var output, types ?? string.Empty, stopOnError) == DdbResult.Success)
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

        throw new DdbException(SafeGetLastError("rescan"));
    }

    #region Multispectral P/Invoke

    [DllImport("ddb", EntryPoint = "DDBGetRasterInfo")]
    private static extern DdbResult _GetRasterInfo(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr output);

    public string GetRasterInfo(string path)
    {
        if (path == null) throw new ArgumentException("path is null");

        try
        {
            if (_GetRasterInfo(path, out var output) == DdbResult.Success)
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

        try
        {
            if (_GetRasterMetadata(path, formula, bandFilter, out var output) == DdbResult.Success)
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

        try
        {
            if (_GenerateMemoryThumbnailEx(filePath, size, preset, bands, formula, bandFilter,
                    colormap, rescale, out var outBuffer, out var outBufferSize) == DdbResult.Success)
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

        try
        {
            if (_GenerateMemoryTileEx(inputPath, tz, tx, ty, tileSize, tms, forceRecreate,
                    inputPathHash, preset, bands, formula, bandFilter, colormap, rescale,
                    out var outBuffer, out var outBufferSize) == DdbResult.Success)
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
        try
        {
            if (_ValidateMergeMultispectral(utf8Ptrs, paths.Length, out var output) == DdbResult.Success)
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
        try
        {
            if (_PreviewMergeMultispectral(utf8Ptrs, paths.Length, previewBands, thumbSize,
                    out var outBuffer, out var outBufferSize) == DdbResult.Success)
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
        try
        {
            if (_MergeMultispectral(utf8Ptrs, paths.Length, outputCog) == DdbResult.Success) return;
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

        throw new DdbException(SafeGetLastError("merge multispectral"));
    }

    #endregion
}