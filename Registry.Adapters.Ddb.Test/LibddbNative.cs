using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Adapters.DroneDB;
using Registry.Test.Common;
using Shouldly;

namespace Registry.Adapters.Ddb.Test;

/// <summary>
/// What the libddb contract tests (<see cref="LibddbContractTests"/>) and the shutdown probe
/// process (<see cref="LibddbShutdownProbe"/>) share: the ABI gate, the native library setup and
/// the handling of native outputs. Assertions are Shouldly's, so they work outside NUnit too.
/// </summary>
internal static class LibddbNative
{
    /// <summary>The outcome of the ABI handshake for the libddb 2.x contract.</summary>
    internal enum Gate
    {
        /// <summary>ABI 2.x (minor at least <see cref="DdbAbi.Minor"/>) and every export Registry calls: the contract applies in full.</summary>
        Applies,

        /// <summary>The adapter cannot use this library (another ABI major, such as a libddb 1.x, or a missing export).</summary>
        Incompatible
    }

    /// <summary>Points the Windows DLL search at the ddb folder, as the other native fixtures do.</summary>
    internal static void UseDdbFolder()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        DdbLibrary.UseFromPath();
    }

    /// <summary>
    /// The startup handshake of the adapter (<see cref="DdbAbi.Verify"/>): Registry runs on
    /// libddb 2.x only, so a library that fails it fails the tests, it never makes them inconclusive.
    /// </summary>
    internal static (Gate Gate, string Message) CheckAbi()
    {
        try
        {
            return (Gate.Applies, DdbAbi.Verify().ToString());
        }
        catch (DdbAbiMismatchException ex)
        {
            return (Gate.Incompatible, ex.Message);
        }
    }

    /// <summary>The message of the last failed call on this thread ("" when it succeeded).</summary>
    internal static string LastError() => DdbResultMapper.GetNativeLastError();

    /// <summary>The reason of the last failed call on this thread ("" for none).</summary>
    internal static string LastErrorReason() => DdbResultMapper.GetNativeLastErrorReason();

    /// <summary>Asserts that a native call succeeded, with its native message otherwise.</summary>
    internal static void Ok(DdbResult result, string call) =>
        result.ShouldBe(DdbResult.Success, $"{call}: {LastError()}");

    /// <summary>Asserts that a native call failed with <paramref name="expected"/> and set a message.</summary>
    internal static void Fails(DdbResult result, DdbResult expected, string call)
    {
        result.ShouldBe(expected, $"{call}: {LastError()}");
        LastError().ShouldNotBeNullOrWhiteSpace($"{call} returned {result} without a message");
    }

    /// <summary>The UTF-8 string output of a native call, freed with DDBFree.</summary>
    internal static string TakeString(IntPtr output)
    {
        output.ShouldNotBe(IntPtr.Zero, "the call succeeded without an output");
        try
        {
            return Marshal.PtrToStringUTF8(output) ?? "";
        }
        finally
        {
            NativeDdb.DDBFree(output);
        }
    }

    /// <summary>The JSON output of a native call, freed with DDBFree.</summary>
    internal static JToken TakeJson(IntPtr output) => ParseJson(TakeString(output));

    /// <summary>The mtime of the contract dataset's files (DroneDB tests/contract): entries get the fixtures' mtimes.</summary>
    internal static readonly DateTime ContractMtime = DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime;

    /// <summary>The contract's a.txt (tests/contract/inputs/a.txt, byte for byte).</summary>
    internal const string TextName = "a.txt";

    internal const string TextContent = "DroneDB contract fixture input\n";

    /// <summary>The name of the GeoRaster in a dataset of <see cref="CreateDataset"/>.</summary>
    internal const string GeoRasterName = "byte.tif";

    /// <summary>
    /// A dataset folder <paramref name="name"/> in <paramref name="area"/> with the contract's a.txt and,
    /// when <paramref name="geoRaster"/> is set, a copy of that GeoRaster as <see cref="GeoRasterName"/>,
    /// both with the contract mtime, indexed.
    /// </summary>
    internal static string CreateDataset(NativeDdbWrapper wrapper, TestArea area, string name, string? geoRaster)
    {
        var root = Path.Combine(area.TestFolder, name);
        Directory.CreateDirectory(root);
        wrapper.Init(root);

        var files = new List<string> { WriteContractFile(root, TextName, TextContent) };
        if (geoRaster != null)
        {
            var raster = Path.Combine(root, GeoRasterName);
            File.Copy(geoRaster, raster);
            File.SetLastWriteTimeUtc(raster, ContractMtime);
            files.Add(raster);
        }

        wrapper.Add(root, files.ToArray());
        return root;
    }

    internal static string WriteContractFile(string root, string name, string content)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, ContractMtime);
        return path;
    }

    /// <summary>JSON as libddb wrote it: strings stay strings (no date parsing), so values compare exactly.</summary>
    internal static JToken ParseJson(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        return JToken.ReadFrom(reader);
    }
}
