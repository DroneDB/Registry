#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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

    [DllImport("ddb", EntryPoint = "DDBGetVersion")]
    private static extern IntPtr _GetVersion();

    public string GetVersion()
    {
        var ptr = _GetVersion();

        var res = Marshal.PtrToStringAnsi(ptr);

        if (string.IsNullOrWhiteSpace(res))
            throw new DdbException("Unable to get version");

        return res;
    }

    [DllImport("ddb", EntryPoint = "DDBGetLastError")]
    private static extern IntPtr _GetLastError();

    static string? GetLastError()
    {
        var ptr = _GetLastError();
        return Marshal.PtrToStringAnsi(ptr);
    }

    static string SafeGetLastError(string? operation = null)
    {
        return GetLastError() ?? (operation != null ? "Unknown error in " + operation : "Unknown error");
    }

    [DllImport("ddb", EntryPoint = "DDBInit")]
    private static extern DdbResult _Init([MarshalAs(UnmanagedType.LPStr)] string directory, out IntPtr outPath);

    public string Init(string directory)
    {
        try
        {
            if (_Init(directory, out var outPath) == DdbResult.Success)
            {
                var res = Marshal.PtrToStringAnsi(outPath);

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

    [DllImport("ddb", EntryPoint = "DDBAdd")]
    private static extern DdbResult _Add([MarshalAs(UnmanagedType.LPStr)] string ddbPath,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)]
        string[] paths,
        int numPaths, out IntPtr output, bool recursive);

    public List<Entry> Add(string ddbPath, string path, bool recursive = false)
    {
        return Add(ddbPath, [path], recursive);
    }

    public List<Entry> Add(string ddbPath, string[] paths, bool recursive = false)
    {
        try
        {
            if (_Add(ddbPath, paths, paths.Length, out var output, recursive) == DdbResult.Success)
            {
                var json = Marshal.PtrToStringAnsi(output);

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
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("add")}\", check inner exception for details",
                ex);
        }

        throw new DdbException(SafeGetLastError("add"));
    }

    [DllImport("ddb", EntryPoint = "DDBRemove")]
    private static extern DdbResult _Remove([MarshalAs(UnmanagedType.LPStr)] string ddbPath,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)]
        string[] paths,
        int numPaths);

    public void Remove(string ddbPath, string path)
    {
        Remove(ddbPath, [path]);
    }

    public void Remove(string ddbPath, string[] paths)
    {
        try
        {
            if (_Remove(ddbPath, paths, paths?.Length ?? 0) == DdbResult.Success) return;
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

        throw new DdbException(SafeGetLastError("remove"));
    }

    [DllImport("ddb", EntryPoint = "DDBInfo")]
    private static extern DdbResult _Info(
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)]
        string[] paths,
        int numPaths,
        out IntPtr output,
        [MarshalAs(UnmanagedType.LPStr)] string format, bool recursive = false,
        int maxRecursionDepth = 0, [MarshalAs(UnmanagedType.LPStr)] string geometry = "auto",
        bool withHash = false, bool stopOnError = true);

    public List<Entry> Info(string path, bool recursive = false, int maxRecursionDepth = 0,
        bool withHash = false)
    {
        return Info([path], recursive, maxRecursionDepth, withHash);
    }

    public List<Entry> Info(string[] paths, bool recursive = false, int maxRecursionDepth = 0,
        bool withHash = false)
    {
        try
        {
            if (_Info(paths, paths?.Length ?? 0, out var output, "json", recursive, maxRecursionDepth, "auto",
                    withHash) ==
                DdbResult.Success)
            {
                var json = Marshal.PtrToStringAnsi(output);

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

        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError("info")}\", check inner exception for details",
                ex);
        }

        throw new DdbException(SafeGetLastError("info"));
    }

    [DllImport("ddb", EntryPoint = "DDBList")]
    private static extern DdbResult _List([MarshalAs(UnmanagedType.LPStr)] string ddbPath,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)]
        string[] paths,
        int numPaths,
        out IntPtr output,
        [MarshalAs(UnmanagedType.LPStr)] string format,
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

        try
        {
            paths = paths.Select(item => item.Replace('\\', '/')).ToArray();
            var lst = _List(ddbPath, paths, paths.Length, out var output, "json", recursive, maxRecursionDepth);

            if (lst == DdbResult.Success)
            {
                var json = Marshal.PtrToStringAnsi(output);

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
        catch (Exception ex)
        {
            throw new DdbException(
                $"Error in calling ddb lib. Last error: \"{SafeGetLastError()}\", check inner exception for details",
                ex);
        }

        throw new DdbException(SafeGetLastError("list"));
    }

    [DllImport("ddb", EntryPoint = "DDBAppendPassword")]
    private static extern DdbResult _AppendPassword(
        [MarshalAs(UnmanagedType.LPStr)] string ddbPath,
        [MarshalAs(UnmanagedType.LPStr)] string password);

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
    static extern DdbResult _VerifyPassword(
        [MarshalAs(UnmanagedType.LPStr)] string ddbPath,
        [MarshalAs(UnmanagedType.LPStr)] string password,
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
    static extern DdbResult _ClearPasswords(
        [MarshalAs(UnmanagedType.LPStr)] string ddbPath);

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
    static extern DdbResult _ChangeAttributes(
        [MarshalAs(UnmanagedType.LPStr)] string ddbPath, [MarshalAs(UnmanagedType.LPStr)] string attributesJson,
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
                var res = Marshal.PtrToStringAnsi(output);

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
    static extern DdbResult _GenerateThumbnail(
        [MarshalAs(UnmanagedType.LPStr)] string filePath, int size, [MarshalAs(UnmanagedType.LPStr)] string destPath);

    public void GenerateThumbnail(string filePath, int size, string destPath)
    {
        if (filePath == null)
            throw new ArgumentException("filePath is null");

        if (destPath == null)
            throw new ArgumentException("destPath is null");

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

        throw new DdbException(SafeGetLastError("generate thumbnail"));
    }

    [DllImport("ddb", EntryPoint = "DDBVSIFree")]
    static extern DdbResult _DDBVSIFree(
        IntPtr buffer);

    [DllImport("ddb", EntryPoint = "DDBGenerateMemoryThumbnail")]
    static extern DdbResult _GenerateMemoryThumbnail(
        [MarshalAs(UnmanagedType.LPStr)] string filePath, int size, out IntPtr outBuffer, out int outBufferSize);

    public byte[] GenerateThumbnail(string filePath, int size)
    {
        if (filePath == null)
            throw new ArgumentException("filePath is null");

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

        throw new DdbException(SafeGetLastError("generate memory thumbnail"));
    }

    [DllImport("ddb", EntryPoint = "DDBTile")]
    static extern DdbResult _GenerateTile(
        [MarshalAs(UnmanagedType.LPStr)] string inputPath, int tz, int tx, int ty, out IntPtr outputTilePath,
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
                var res = Marshal.PtrToStringAnsi(output);

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
    static extern DdbResult _GenerateMemoryTile(
        [MarshalAs(UnmanagedType.LPStr)] string inputPath, int tz, int tx, int ty, out IntPtr outBuffer,
        out int outBufferSize, int tileSize, bool tms, bool forceRecreate,
        [MarshalAs(UnmanagedType.LPStr)] string inputPathHash);

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
    static extern DdbResult _SetTag([MarshalAs(UnmanagedType.LPStr)] string ddbPath,
        [MarshalAs(UnmanagedType.LPStr)] string newTag);

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
    static extern DdbResult _GetTag([MarshalAs(UnmanagedType.LPStr)] string ddbPath, out IntPtr outTag);

    public string? GetTag(string ddbPath)
    {
        if (ddbPath == null)
            throw new ArgumentException("DDB path is null");

        try
        {
            if (_GetTag(ddbPath, out var outTag) !=
                DdbResult.Success) throw new DdbException(SafeGetLastError());

            var res = Marshal.PtrToStringAnsi(outTag);

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
    static extern DdbResult _DDBGetStamp([MarshalAs(UnmanagedType.LPStr)] string ddbPath, out IntPtr output);

    public Stamp GetStamp(string ddbPath)
    {
        if (ddbPath == null)
            throw new ArgumentException("DDB path is null");

        try
        {
            if (_DDBGetStamp(ddbPath, out var output) ==
                DdbResult.Success)
            {
                var json = Marshal.PtrToStringAnsi(output);

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
    private static extern DdbResult _Delta([MarshalAs(UnmanagedType.LPStr)] string ddbSourceStamp,
        [MarshalAs(UnmanagedType.LPStr)] string ddbTargetStamp, out IntPtr output,
        [MarshalAs(UnmanagedType.LPStr)] string format);

    public Delta Delta(string ddbPath, string ddbTarget)
    {
        return Delta(GetStamp(ddbPath), GetStamp(ddbTarget));
    }

    [DllImport("ddb", EntryPoint = "DDBApplyDelta")]
    private static extern DdbResult _ApplyDelta([MarshalAs(UnmanagedType.LPStr)] string delta,
        [MarshalAs(UnmanagedType.LPStr)] string sourcePath,
        [MarshalAs(UnmanagedType.LPStr)] string ddbPath, int mergeStrategy,
        [MarshalAs(UnmanagedType.LPStr)] string sourceMetaDump, out IntPtr conflicts);

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
                var conflicts = Marshal.PtrToStringAnsi(conflictsPtr);

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
                var json = Marshal.PtrToStringAnsi(output);

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
    private static extern DdbResult _ComputeDeltaLocals([MarshalAs(UnmanagedType.LPStr)] string delta,
        [MarshalAs(UnmanagedType.LPStr)] string ddbPath, [MarshalAs(UnmanagedType.LPStr)] string hlDestFolder,
        out IntPtr output);

    public Dictionary<string, bool> ComputeDeltaLocals(Delta delta, string ddbPath, string hlDestFolder = "")
    {
        try
        {
            var deltaJson = JsonConvert.SerializeObject(delta);

            if (_ComputeDeltaLocals(deltaJson, ddbPath, hlDestFolder, out var outputPtr) ==
                DdbResult.Success)
            {
                var output = Marshal.PtrToStringAnsi(outputPtr);

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
    private static extern DdbResult _MoveEntry([MarshalAs(UnmanagedType.LPStr)] string ddbSource,
        [MarshalAs(UnmanagedType.LPStr)] string source, [MarshalAs(UnmanagedType.LPStr)] string dest);

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
    private static extern DdbResult _Build([MarshalAs(UnmanagedType.LPStr)] string ddbSource,
        [MarshalAs(UnmanagedType.LPStr)] string? source, [MarshalAs(UnmanagedType.LPStr)] string? dest, bool force,
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
    private static extern DdbResult _IsBuildable([MarshalAs(UnmanagedType.LPStr)] string ddbSource,
        [MarshalAs(UnmanagedType.LPStr)] string path, out bool isBuildable);

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

    [DllImport("ddb", EntryPoint = "DDBIsBuildPending")]
    private static extern DdbResult _IsBuildPending([MarshalAs(UnmanagedType.LPStr)] string ddbPath,
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
    static extern DdbResult _MetaAdd([MarshalAs(UnmanagedType.LPStr)] string ddbPath,
        [MarshalAs(UnmanagedType.LPStr)] string path,
        [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPStr)] string data, out IntPtr output);

    public Meta MetaAdd(string ddbPath, string key, string data, string? path = null)
    {
        try
        {
            if (_MetaAdd(ddbPath, path ?? string.Empty, key, data, out var output) ==
                DdbResult.Success)
            {
                var json = Marshal.PtrToStringAnsi(output);

                if (json == null)
                    throw new InvalidOperationException("No result from DDBMetaUnset call");

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
    static extern DdbResult _MetaSet([MarshalAs(UnmanagedType.LPStr)] string ddbPath,
        [MarshalAs(UnmanagedType.LPStr)] string path,
        [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPStr)] string data, out IntPtr output);

    public Meta MetaSet(string ddbPath, string key, string data, string? path = null)
    {
        try
        {
            if (_MetaSet(ddbPath, path ?? string.Empty, key, data, out var output) ==
                DdbResult.Success)
            {
                var json = Marshal.PtrToStringAnsi(output);
                if (json == null)
                    throw new InvalidOperationException("No result from DDBMetaUnset call");

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
    static extern DdbResult _MetaRemove([MarshalAs(UnmanagedType.LPStr)] string ddbPath,
        [MarshalAs(UnmanagedType.LPStr)] string id, out IntPtr output);

    public int MetaRemove(string ddbPath, string id)
    {
        try
        {
            if (_MetaRemove(ddbPath, id, out var output) ==
                DdbResult.Success)
            {
                var json = Marshal.PtrToStringAnsi(output);

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
    static extern DdbResult _MetaGet([MarshalAs(UnmanagedType.LPStr)] string ddbPath,
        [MarshalAs(UnmanagedType.LPStr)] string path,
        [MarshalAs(UnmanagedType.LPStr)] string key, out IntPtr output);

    public string? MetaGet(string ddbPath, string key, string? path = null)
    {
        try
        {
            if (_MetaGet(ddbPath, path ?? string.Empty, key, out var output) ==
                DdbResult.Success)
            {
                var json = Marshal.PtrToStringAnsi(output);

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
    static extern DdbResult _MetaUnset([MarshalAs(UnmanagedType.LPStr)] string ddbPath,
        [MarshalAs(UnmanagedType.LPStr)] string path,
        [MarshalAs(UnmanagedType.LPStr)] string key, out IntPtr output);

    public int MetaUnset(string ddbPath, string key, string? path = null)
    {
        try
        {
            if (_MetaUnset(ddbPath, path ?? string.Empty, key, out var output) ==
                DdbResult.Success)
            {
                var json = Marshal.PtrToStringAnsi(output);

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
    static extern DdbResult _MetaList([MarshalAs(UnmanagedType.LPStr)] string ddbPath,
        [MarshalAs(UnmanagedType.LPStr)] string path, out IntPtr output);

    public List<MetaListItem> MetaList(string ddbPath, string? path = null)
    {
        try
        {
            if (_MetaList(ddbPath, path ?? string.Empty, out var output) ==
                DdbResult.Success)
            {
                var json = Marshal.PtrToStringAnsi(output);

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
    static extern DdbResult _MetaDump([MarshalAs(UnmanagedType.LPStr)] string ddbPath,
        [MarshalAs(UnmanagedType.LPStr)] string ids, out IntPtr output);

    public List<MetaDump> MetaDump(string ddbPath, string? ids = null)
    {
        try
        {
            if (_MetaDump(ddbPath, ids ?? "[]", out var output) ==
                DdbResult.Success)
            {
                var json = Marshal.PtrToStringAnsi(output);

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
    static extern DdbResult _Stac([MarshalAs(UnmanagedType.LPStr)] string ddbPath,
        [MarshalAs(UnmanagedType.LPStr)] string? entry,
        [MarshalAs(UnmanagedType.LPStr)] string stacCollectionRoot, [MarshalAs(UnmanagedType.LPStr)] string id,
        [MarshalAs(UnmanagedType.LPStr)] string stacCatalogRoot, out IntPtr output);

    public JToken Stac(string ddbPath, string? entry, string stacCollectionRoot, string id,
        string stacCatalogRoot)
    {
        try
        {
            if (_Stac(ddbPath, entry ?? string.Empty, stacCollectionRoot, id, stacCatalogRoot, out var output) ==
                DdbResult.Success)
            {
                var json = Marshal.PtrToStringAnsi(output);

                if (json == null)
                    throw new InvalidOperationException("No result from DDBMetaDump call");

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
}