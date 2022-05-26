using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Ports.DroneDB.Models;


namespace Registry.Adapters.DroneDB
{
    public static class DDBWrapper
    {
        [DllImport("ddb", EntryPoint = "DDBRegisterProcess")]
        public static extern void RegisterProcess(bool verbose = false);

        [DllImport("ddb", EntryPoint = "DDBGetVersion")]
        private static extern IntPtr _GetVersion();

        public static string GetVersion()
        {
            var ptr = _GetVersion();
            return Marshal.PtrToStringAnsi(ptr);
        }

        [DllImport("ddb", EntryPoint = "DDBGetLastError")]
        private static extern IntPtr _GetLastError();

        static string GetLastError()
        {
            var ptr = _GetLastError();
            return Marshal.PtrToStringAnsi(ptr);
        }

        [DllImport("ddb", EntryPoint = "DDBInit")]
        private static extern DDBError _Init([MarshalAs(UnmanagedType.LPStr)] string directory, out IntPtr outPath);

        public static string Init(string directory)
        {

            try
            {

                if (_Init(directory, out var outPath) == DDBError.DDBERR_NONE)
                    return Marshal.PtrToStringAnsi(outPath);

            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }

            throw new DDBException(GetLastError());

        }

        [DllImport("ddb", EntryPoint = "DDBAdd")]
        private static extern DDBError _Add([MarshalAs(UnmanagedType.LPStr)] string ddbPath,
                                  [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] paths,
                                  int numPaths, out IntPtr output, bool recursive);

        public static List<Entry> Add(string ddbPath, string path, bool recursive = false)
        {
            return Add(ddbPath, path != null ? new[] { path } : null, recursive);
        }

        public static List<Entry> Add(string ddbPath, string[] paths, bool recursive = false)
        {

            try
            {
                if (_Add(ddbPath, paths, paths?.Length ?? 0, out var output, recursive) != DDBError.DDBERR_NONE)
                    throw new DDBException(GetLastError());

                var json = Marshal.PtrToStringAnsi(output);

                if (string.IsNullOrWhiteSpace(json))
                    throw new DDBException("Unable to add");

                return JsonConvert.DeserializeObject<List<Entry>>(json);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }

        }

        [DllImport("ddb", EntryPoint = "DDBRemove")]
        private static extern DDBError _Remove([MarshalAs(UnmanagedType.LPStr)] string ddbPath,
                                  [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] paths,
                                  int numPaths);

        public static void Remove(string ddbPath, string path)
        {
            Remove(ddbPath, path != null ? new[] { path } : null);
        }
        public static void Remove(string ddbPath, string[] paths)
        {
            try
            {
                if (_Remove(ddbPath, paths, paths?.Length ?? 0) != DDBError.DDBERR_NONE)
                    throw new DDBException(GetLastError());
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }

            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }
        }

        [DllImport("ddb", EntryPoint = "DDBInfo")]
        private static extern DDBError _Info([MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] paths,
                                   int numPaths,
                                   out IntPtr output,
                                   [MarshalAs(UnmanagedType.LPStr)] string format, bool recursive = false,
                                   int maxRecursionDepth = 0, [MarshalAs(UnmanagedType.LPStr)] string geometry = "auto",
                                   bool withHash = false, bool stopOnError = true);

        public static List<Entry> Info(string path, bool recursive = false, int maxRecursionDepth = 0, bool withHash = false)
        {
            return Info(path != null ? new[] { path } : null, recursive, maxRecursionDepth, withHash);
        }

        public static List<Entry> Info(string[] paths, bool recursive = false, int maxRecursionDepth = 0, bool withHash = false)
        {

            try
            {
                if (_Info(paths, paths?.Length ?? 0, out var output, "json", recursive, maxRecursionDepth, "auto", withHash) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

                var json = Marshal.PtrToStringAnsi(output);

                if (string.IsNullOrWhiteSpace(json))
                    throw new DDBException("Unable get info");

                return JsonConvert.DeserializeObject<List<Entry>>(json);

            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }

            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }
        }

        [DllImport("ddb", EntryPoint = "DDBList")]
        private static extern DDBError _List([MarshalAs(UnmanagedType.LPStr)] string ddbPath,
                                    [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] paths,
                                    int numPaths,
                                    out IntPtr output,
                                    [MarshalAs(UnmanagedType.LPStr)] string format,
                                    bool recursive,
                                    int maxRecursionDepth = 0);

        public static List<Entry> List(string ddbPath, string path = "", bool recursive = false, int maxRecursionDepth = 0)
        {
            return List(ddbPath, path != null ? new[] { path } : null, recursive, maxRecursionDepth);
        }

        public static List<Entry> List(string ddbPath, string[] paths, bool recursive = false, int maxRecursionDepth = 0)
        {
            try
            {
                
                IntPtr output;
                DDBError res;

                if (paths != null && paths.Length != 0)
                {
                    paths = paths.Select(item => item.Replace('\\', '/')).ToArray();
                    res = _List(ddbPath, paths, paths.Length, out output, "json", recursive, maxRecursionDepth);
                }
                else
                    res = _List(ddbPath, null, 0, out output, "json", recursive, maxRecursionDepth);

                if (res != DDBError.DDBERR_NONE)
                    throw new DDBException(GetLastError());
                
                var json = Marshal.PtrToStringAnsi(output);

                if (string.IsNullOrWhiteSpace(json))
                    throw new DDBException("Unable get list");

                return JsonConvert.DeserializeObject<List<Entry>>(json);

            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }

        }

        [DllImport("ddb", EntryPoint = "DDBAppendPassword")]
        private static extern DDBError _AppendPassword(
            [MarshalAs(UnmanagedType.LPStr)] string ddbPath,
            [MarshalAs(UnmanagedType.LPStr)] string password);

        public static void AppendPassword(string ddbPath, string password)
        {
            try
            {

                if (_AppendPassword(ddbPath, password) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }
        }

        [DllImport("ddb", EntryPoint = "DDBVerifyPassword")]
        static extern DDBError _VerifyPassword(
            [MarshalAs(UnmanagedType.LPStr)] string ddbPath,
            [MarshalAs(UnmanagedType.LPStr)] string password,
            out bool verified);

        public static bool VerifyPassword(string ddbPath, string password)
        {
            try
            {

                if (_VerifyPassword(ddbPath, password, out var res) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

                return res;
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }

        }

        [DllImport("ddb", EntryPoint = "DDBClearPasswords")]
        static extern DDBError _ClearPasswords(
            [MarshalAs(UnmanagedType.LPStr)] string ddbPath);

        public static void ClearPasswords(string ddbPath)
        {
            try
            {

                if (_ClearPasswords(ddbPath) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }

        }

        [DllImport("ddb", EntryPoint = "DDBChattr")]
        static extern DDBError _ChangeAttributes(
            [MarshalAs(UnmanagedType.LPStr)] string ddbPath, [MarshalAs(UnmanagedType.LPStr)] string attributesJson, out IntPtr jsonOutput);

        public static Dictionary<string, object> ChangeAttributes(string ddbPath, Dictionary<string, object> attributes)
        {

            if (attributes == null)
                throw new ArgumentException("Attributes is null");

            try
            {

                var attrs = JsonConvert.SerializeObject(attributes);

                if (_ChangeAttributes(ddbPath, attrs, out var output) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

                var res = Marshal.PtrToStringAnsi(output);

                if (string.IsNullOrWhiteSpace(res))
                    throw new DDBException("Unable get attributes");

                return JsonConvert.DeserializeObject<Dictionary<string, object>>(res);

            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }

        }

        public static Dictionary<string, object> GetAttributes(string ddbPath)
        {
            return ChangeAttributes(ddbPath, new Dictionary<string, object>());
        }

        [DllImport("ddb", EntryPoint = "DDBGenerateThumbnail")]
        static extern DDBError _GenerateThumbnail(
            [MarshalAs(UnmanagedType.LPStr)] string filePath, int size, [MarshalAs(UnmanagedType.LPStr)] string destPath);

        public static void GenerateThumbnail(string filePath, int size, string destPath)
        {

            if (filePath == null)
                throw new ArgumentException("filePath is null");

            if (destPath == null)
                throw new ArgumentException("destPath is null");

            try
            {

                if (_GenerateThumbnail(filePath, size, destPath) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }

        }

        [DllImport("ddb", EntryPoint = "DDBVSIFree")]
        static extern DDBError _DDBVSIFree(
            IntPtr buffer);

        [DllImport("ddb", EntryPoint = "DDBGenerateMemoryThumbnail")]
        static extern DDBError _GenerateMemoryThumbnail(
            [MarshalAs(UnmanagedType.LPStr)] string filePath, int size, out IntPtr outBuffer, out int outBufferSize);

        public static byte[] GenerateThumbnail(string filePath, int size)
        {

            if (filePath == null)
                throw new ArgumentException("filePath is null");

            try
            {
                if (_GenerateMemoryThumbnail(filePath, size, out var outBuffer, out var outBufferSize) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

                var destBuf = new byte[outBufferSize];
                Marshal.Copy(outBuffer, destBuf, 0, outBufferSize);

                _DDBVSIFree(outBuffer);

                return destBuf;
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }

        }

        [DllImport("ddb", EntryPoint = "DDBTile")]
        static extern DDBError _GenerateTile(
            [MarshalAs(UnmanagedType.LPStr)] string inputPath, int tz, int tx, int ty, out IntPtr outputTilePath, int tileSize, bool tms, bool forceRecreate);

        public static string GenerateTile(string inputPath, int tz, int tx, int ty, int tileSize, bool tms, bool forceRecreate = false)
        {

            if (inputPath == null)
                throw new ArgumentException("inputPath is null");

            try
            {

                if (_GenerateTile(inputPath, tz, tx, ty, out var output, tileSize, tms, forceRecreate) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

                var res = Marshal.PtrToStringAnsi(output);

                if (string.IsNullOrWhiteSpace(res))
                    throw new DDBException("Unable get tile path");

                return res;

            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }

        }

        [DllImport("ddb", EntryPoint = "DDBMemoryTile")]
        static extern DDBError _GenerateMemoryTile(
            [MarshalAs(UnmanagedType.LPStr)] string inputPath, int tz, int tx, int ty, out IntPtr outBuffer, out int outBufferSize, int tileSize, bool tms, bool forceRecreate, [MarshalAs(UnmanagedType.LPStr)] string inputPathHash);
        public static byte[] GenerateMemoryTile(string inputPath, int tz, int tx, int ty, int tileSize, bool tms, bool forceRecreate = false, string inputPathHash = "")
        {
            if (inputPath == null)
                throw new ArgumentException("inputPath is null");

            try
            {
                if (_GenerateMemoryTile(inputPath, tz, tx, ty, out var outBuffer, out var outBufferSize, tileSize, tms, forceRecreate, inputPathHash) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

                var destBuf = new byte[outBufferSize];
                Marshal.Copy(outBuffer, destBuf, 0, outBufferSize);

                _DDBVSIFree(outBuffer);

                return destBuf;

            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }

        }

        [DllImport("ddb", EntryPoint = "DDBSetTag")]
        static extern DDBError _SetTag([MarshalAs(UnmanagedType.LPStr)] string ddbPath, [MarshalAs(UnmanagedType.LPStr)] string newTag);

        public static void SetTag(string ddbPath, string newTag)
        {

            if (ddbPath == null)
                throw new ArgumentException("DDB path is null");

            if (newTag == null)
                throw new ArgumentException("New tag is null");

            try
            {

                if (_SetTag(ddbPath, newTag) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }

        }

        [DllImport("ddb", EntryPoint = "DDBGetTag")]
        static extern DDBError _GetTag([MarshalAs(UnmanagedType.LPStr)] string ddbPath, out IntPtr outTag);

        public static string GetTag(string ddbPath)
        {

            if (ddbPath == null)
                throw new ArgumentException("DDB path is null");

            try
            {

                if (_GetTag(ddbPath, out var outTag) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

                var res = Marshal.PtrToStringAnsi(outTag);

                return res == null || string.IsNullOrWhiteSpace(res) ? null : res;

            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }

        }

        [DllImport("ddb", EntryPoint = "DDBGetStamp")]
        static extern DDBError _DDBGetStamp([MarshalAs(UnmanagedType.LPStr)] string ddbPath, out IntPtr output);

        public static Stamp GetStamp(string ddbPath)
        {
            if (ddbPath == null)
                throw new ArgumentException("DDB path is null");

            try
            {
                if (_DDBGetStamp(ddbPath, out var output) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

                var json = Marshal.PtrToStringAnsi(output);

                if (json == null)
                    throw new InvalidOperationException("No result from DDBGetStamp call");

                return JsonConvert.DeserializeObject<Stamp>(json);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }
        }

        [DllImport("ddb", EntryPoint = "DDBDelta")]
        private static extern DDBError _Delta([MarshalAs(UnmanagedType.LPStr)] string ddbSourceStamp,
            [MarshalAs(UnmanagedType.LPStr)] string ddbTargetStamp, out IntPtr output, [MarshalAs(UnmanagedType.LPStr)] string format);

        public static Delta Delta(string ddbPath, string ddbTarget)
        {
            return Delta(GetStamp(ddbPath), GetStamp(ddbTarget));
        }


        [DllImport("ddb", EntryPoint = "DDBApplyDelta")]
        private static extern DDBError _ApplyDelta([MarshalAs(UnmanagedType.LPStr)] string delta,
            [MarshalAs(UnmanagedType.LPStr)] string sourcePath, 
            [MarshalAs(UnmanagedType.LPStr)] string ddbPath, int mergeStrategy,
            [MarshalAs(UnmanagedType.LPStr)] string sourceMetaDump, out IntPtr conflicts);

        public static List<string> ApplyDelta(Delta delta, string sourcePath, string ddbPath, MergeStrategy mergeStrategy, string sourceMetaDump = null)
        {
            try
            {
                string deltaJson = JsonConvert.SerializeObject(delta);

                if (_ApplyDelta(deltaJson, sourcePath, ddbPath, (int)mergeStrategy, sourceMetaDump ?? "[]", out var conflictsPtr) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

                var conflicts = Marshal.PtrToStringAnsi(conflictsPtr);

                if (string.IsNullOrWhiteSpace(conflicts))
                    throw new DDBException("Unable get applydelta result");

                return JsonConvert.DeserializeObject<List<string>>(conflicts);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException(
                    $"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details",
                    ex);
            }
        }



        public static Delta Delta(Stamp source, Stamp target)
        {
            try
            {
                string sourceJson = JsonConvert.SerializeObject(source);
                string targetJson = JsonConvert.SerializeObject(target);

                if (_Delta(sourceJson, targetJson, out var output, "json") !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

                var json = Marshal.PtrToStringAnsi(output);

                if (string.IsNullOrWhiteSpace(json))
                    throw new DDBException("Unable get delta");

                return JsonConvert.DeserializeObject<Delta>(json);

            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException(
                    $"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details",
                    ex);
            }

        }


        [DllImport("ddb", EntryPoint = "DDBComputeDeltaLocals")]
        private static extern DDBError _ComputeDeltaLocals([MarshalAs(UnmanagedType.LPStr)] string delta,
    [MarshalAs(UnmanagedType.LPStr)] string ddbPath, [MarshalAs(UnmanagedType.LPStr)] string hlDestFolder,
    out IntPtr output);

        public static Dictionary<string, bool> ComputeDeltaLocals(Delta delta, string ddbPath, string hlDestFolder = "")
        {
            try
            {
                string deltaJson = JsonConvert.SerializeObject(delta);

                if (_ComputeDeltaLocals(deltaJson, ddbPath, hlDestFolder, out var outputPtr) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

                var output = Marshal.PtrToStringAnsi(outputPtr);

                if (string.IsNullOrWhiteSpace(output))
                    throw new DDBException("Unable get ComputeDeltaLocals result");

                return JsonConvert.DeserializeObject<Dictionary<string, bool>>(output);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException(
                    $"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details",
                    ex);
            }
        }


        [DllImport("ddb", EntryPoint = "DDBMoveEntry")]
        private static extern DDBError _MoveEntry([MarshalAs(UnmanagedType.LPStr)] string ddbSource,
            [MarshalAs(UnmanagedType.LPStr)] string source, [MarshalAs(UnmanagedType.LPStr)] string dest);

        public static void MoveEntry(string ddbPath, string source, string dest)
        {

            try
            {

                if (_MoveEntry(ddbPath, source, dest) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }

        }

        [DllImport("ddb", EntryPoint = "DDBBuild")]
        private static extern DDBError _Build([MarshalAs(UnmanagedType.LPStr)] string ddbSource,
            [MarshalAs(UnmanagedType.LPStr)] string source, [MarshalAs(UnmanagedType.LPStr)] string dest, bool force, bool pendingOnly);

        public static void Build(string ddbPath, string source = null, string dest = null, bool force = false, bool pendingOnly = false)
        {
            try
            {
                var result = _Build(ddbPath, source, dest, force, pendingOnly);
                if (result == DDBError.DDBERR_EXCEPTION) throw new DDBException(GetLastError());
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }

        }

        [DllImport("ddb", EntryPoint = "DDBIsBuildable")]
        private static extern DDBError _IsBuildable([MarshalAs(UnmanagedType.LPStr)] string ddbSource,
            [MarshalAs(UnmanagedType.LPStr)] string path, out bool isBuildable);

        public static bool IsBuildable(string ddbPath, string path)
        {

            try
            {

                if (_IsBuildable(ddbPath, path, out bool isBuildable) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

                return isBuildable;

            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }

        }

        [DllImport("ddb", EntryPoint = "DDBIsBuildPending")]
        private static extern DDBError _IsBuildPending([MarshalAs(UnmanagedType.LPStr)] string ddbPath, out bool isBuildPending);

        public static bool IsBuildPending(string ddbPath)
        {

            try
            {

                if (_IsBuildPending(ddbPath, out bool isBuildPending) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

                return isBuildPending;

            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }

        }

        [DllImport("ddb", EntryPoint = "DDBMetaAdd")]
        static extern DDBError _MetaAdd([MarshalAs(UnmanagedType.LPStr)] string ddbPath, [MarshalAs(UnmanagedType.LPStr)] string path, 
            [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPStr)] string data, out IntPtr output);

        public static Meta MetaAdd(string ddbPath, string key, string data, string path = null)
        {
            try
            {
                if (_MetaAdd(ddbPath, path ?? string.Empty, key, data, out var output) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

                var json = Marshal.PtrToStringAnsi(output);

                if (json == null)
                    throw new InvalidOperationException("No result from DDBMetaUnset call");
                
                return JsonConvert.DeserializeObject<Meta>(json);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }

        }

        [DllImport("ddb", EntryPoint = "DDBMetaSet")]
        static extern DDBError _MetaSet([MarshalAs(UnmanagedType.LPStr)] string ddbPath, [MarshalAs(UnmanagedType.LPStr)] string path, 
            [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPStr)] string data, out IntPtr output);

        public static Meta MetaSet(string ddbPath, string key, string data, string path = null)
        {
            try
            {
                if (_MetaSet(ddbPath, path ?? string.Empty, key, data, out var output) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

                var json = Marshal.PtrToStringAnsi(output);
                if (json == null)
                    throw new InvalidOperationException("No result from DDBMetaUnset call");
                
                return JsonConvert.DeserializeObject<Meta>(json);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }

        }

        [DllImport("ddb", EntryPoint = "DDBMetaRemove")]
        static extern DDBError _MetaRemove([MarshalAs(UnmanagedType.LPStr)] string ddbPath, 
            [MarshalAs(UnmanagedType.LPStr)] string id, out IntPtr output);

        public static int MetaRemove(string ddbPath, string id)
        {
            try
            {
                if (_MetaRemove(ddbPath, id, out var output) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

                var json = Marshal.PtrToStringAnsi(output);

                if (json == null)
                    throw new InvalidOperationException("No result from DDBMetaRemove call");

                var obj = JsonConvert.DeserializeObject<JObject>(json);

                if (obj == null || !obj.ContainsKey("removed"))
                    throw new InvalidOperationException($"Expected 'removed' field but got '{json}'");

                // ReSharper disable once PossibleNullReferenceException
                return obj["removed"].ToObject<int>();

            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }

        }

        [DllImport("ddb", EntryPoint = "DDBMetaGet")]
        static extern DDBError _MetaGet([MarshalAs(UnmanagedType.LPStr)] string ddbPath, [MarshalAs(UnmanagedType.LPStr)] string path, 
            [MarshalAs(UnmanagedType.LPStr)] string key, out IntPtr output);

        public static string MetaGet(string ddbPath, string key, string path = null)
        {
            try
            {
                if (_MetaGet(ddbPath, path ?? string.Empty, key, out var output) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

                var json = Marshal.PtrToStringAnsi(output);

                return json;
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }
        }
        
        [DllImport("ddb", EntryPoint = "DDBMetaUnset")]
        static extern DDBError _MetaUnset([MarshalAs(UnmanagedType.LPStr)] string ddbPath, [MarshalAs(UnmanagedType.LPStr)] string path, 
            [MarshalAs(UnmanagedType.LPStr)] string key, out IntPtr output);

        public static int MetaUnset(string ddbPath, string key, string path = null)
        {
            try
            {
                if (_MetaUnset(ddbPath, path ?? string.Empty, key, out var output) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

                var json = Marshal.PtrToStringAnsi(output);

                if (json == null) 
                    throw new InvalidOperationException("No result from DDBMetaUnset call");

                var obj = JsonConvert.DeserializeObject<JObject>(json);

                if (obj == null || !obj.ContainsKey("removed"))
                    throw new InvalidOperationException($"Expected 'removed' field but got '{json}'");

                // ReSharper disable once PossibleNullReferenceException
                return obj["removed"].ToObject<int>();

            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }
        }


        [DllImport("ddb", EntryPoint = "DDBMetaList")]
        static extern DDBError _MetaList([MarshalAs(UnmanagedType.LPStr)] string ddbPath, 
            [MarshalAs(UnmanagedType.LPStr)] string path, out IntPtr output);

        public static List<MetaListItem> MetaList(string ddbPath, string path = null)
        {
            try
            {
                if (_MetaList(ddbPath, path ?? string.Empty, out var output) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

                var json = Marshal.PtrToStringAnsi(output);

                if (json == null)
                    throw new InvalidOperationException("No result from DDBMetaList call");

                return JsonConvert.DeserializeObject<List<MetaListItem>>(json);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }
        }

        [DllImport("ddb", EntryPoint = "DDBMetaDump")]
        static extern DDBError _MetaDump([MarshalAs(UnmanagedType.LPStr)] string ddbPath,
         [MarshalAs(UnmanagedType.LPStr)] string ids, out IntPtr output);

        public static List<MetaDump> MetaDump(string ddbPath, string ids = null)
        {
            try
            {
                if (_MetaDump(ddbPath, ids ?? "[]", out var output) !=
                    DDBError.DDBERR_NONE) throw new DDBException(GetLastError());

                var json = Marshal.PtrToStringAnsi(output);

                if (json == null)
                    throw new InvalidOperationException("No result from DDBMetaDump call");

                return JsonConvert.DeserializeObject<List<MetaDump>>(json);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DDBException($"Error in calling ddb lib: incompatible versions ({ex.Message})", ex);
            }
            catch (Exception ex)
            {
                throw new DDBException($"Error in calling ddb lib. Last error: \"{GetLastError()}\", check inner exception for details", ex);
            }
        }
    }
}
