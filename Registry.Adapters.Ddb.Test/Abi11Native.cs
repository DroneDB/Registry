#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Shouldly;

namespace Registry.Adapters.Ddb.Test;

/// <summary>
/// What the ABI 1.1 contract tests (<see cref="Abi11ContractTests"/>) and the shutdown probe
/// process (<see cref="Abi11ShutdownProbe"/>) share: the ABI gate, the native library setup and
/// the handling of native outputs. Assertions are Shouldly's, so they work outside NUnit too.
/// </summary>
internal static class Abi11Native
{
    /// <summary>The outcome of the ABI handshake for the ABI 1.1 contract.</summary>
    internal enum Gate
    {
        /// <summary>ABI 1.x with x &gt;= 1: every ABI 1.1 export must exist and work.</summary>
        Applies,

        /// <summary>ABI 1.0 (no <c>DDBGetAbiVersion</c>): the ABI 1.1 contract does not apply.</summary>
        NotApplicable,

        /// <summary>Another ABI major: the adapter cannot use this library at all.</summary>
        Incompatible
    }

    /// <summary>Points the Windows DLL search at the ddb folder, as the other native fixtures do.</summary>
    internal static void UseDdbFolder()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        var ddbFolder = CommonUtils.FindDdbFolder() ?? throw new InvalidOperationException("DDB not found");
        CommonUtils.SetDefaultDllPath(ddbFolder);
    }

    /// <summary>Checks the loaded libddb against the ABI these tests are written for (see the rule in <see cref="Abi11ContractTests"/>).</summary>
    internal static (Gate Gate, string Message) CheckAbi()
    {
        var loaded = DdbAbi.Loaded();
        if (loaded is null)
            return (Gate.NotApplicable,
                $"libddb {new NativeDdbWrapper().GetVersion()} has no DDBGetAbiVersion (ABI 1.0): the ABI 1.1 " +
                "contract does not apply. It runs against libddb >= 1.13 (DroneDB's registry_contract job).");

        var (major, minor) = loaded.Value;
        if (major != DdbAbi.Major)
            return (Gate.Incompatible,
                $"libddb reports ABI {major}.{minor}: Registry's adapter is written for ABI {DdbAbi.Major}.x");

        return minor < DdbAbi.Minor
            ? (Gate.NotApplicable, $"libddb reports ABI {major}.{minor}: the ABI {DdbAbi.Major}.{DdbAbi.Minor} contract does not apply")
            : (Gate.Applies, $"libddb ABI {major}.{minor}");
    }

    /// <summary>The message of the last failed call on this thread ("" when it succeeded).</summary>
    internal static string LastError() => DdbResultMapper.GetNativeLastError() ?? "";

    /// <summary>The reason of the last failed call on this thread ("" for none).</summary>
    internal static string LastErrorReason() => Marshal.PtrToStringUTF8(NativeDdbAbi11.DDBGetLastErrorReason()) ?? "";

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
            NativeDdbAbi11.DDBFree(output);
        }
    }

    /// <summary>The JSON output of a native call, freed with DDBFree.</summary>
    internal static JToken TakeJson(IntPtr output) => ParseJson(TakeString(output));

    /// <summary>JSON as libddb wrote it: strings stay strings (no date parsing), so values compare exactly.</summary>
    internal static JToken ParseJson(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        return JToken.ReadFrom(reader);
    }
}
