#nullable enable
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Shouldly;
using static Registry.Adapters.Ddb.Test.Abi11Native;

namespace Registry.Adapters.Ddb.Test;

/// <summary>
/// The test assembly's entry point. <c>dotnet test</c> never calls it; the shutdown contract test
/// starts this assembly with <see cref="Abi11ShutdownProbe.Argument"/> to run DDBShutdown in a
/// process of its own (GenerateProgramFile is off in the csproj for this).
/// </summary>
internal static class Program
{
    public static int Main(string[] args) => args switch
    {
        [] => 0,
        [Abi11ShutdownProbe.Argument] => Abi11ShutdownProbe.Run(),
        _ => 2
    };
}

/// <summary>
/// DDBShutdown (ABI 1.1, DQ-15/XI-07) in a child process, since no call but the exempt accessors
/// works after it. Run by <see cref="Abi11ContractTests"/>; prints one line per step and
/// <see cref="Passed"/> at the end. Exit codes: 0 passed, 1 a step failed, 3 the loaded libddb is
/// not ABI 1.1 (the parent test checks the gate first, so 3 is a failure there too).
/// </summary>
internal static unsafe class Abi11ShutdownProbe
{
    public const string Argument = "--abi11-shutdown-probe";
    public const string Passed = "ABI 1.1 SHUTDOWN PROBE PASSED";

    private const int NotRun = int.MinValue;
    private static int _shutdownFromCallback = NotRun;

    public static int Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), nameof(Abi11ShutdownProbe), Guid.NewGuid().ToString("N"));
        try
        {
            UseDdbFolder();
            var (gate, message) = CheckAbi();
            Console.WriteLine($"gate: {gate} ({message})");
            if (gate != Gate.Applies)
                return 3;

            Ok(NativeDdb.DDBRegisterProcess(false), "DDBRegisterProcess");
            Step("DDBRegisterProcess returns DDBERR_NONE");

            // A dataset with a pooled index connection, for the checkpoint and close
            var wrapper = new NativeDdbWrapper();
            Directory.CreateDirectory(folder);
            wrapper.Init(folder);
            var file = Path.Combine(folder, "a.txt");
            File.WriteAllText(file, "a");
            wrapper.Add(folder, [file]);
            Ok(NativeDdb.DDBListEntriesEx(folder, "", "", "", DdbListEntriesOptions.Create(true, false, true),
                out var listing), "DDBListEntriesEx");
            TakeString(listing);
            Step("dataset indexed and listed");

            ShutdownFromTheLogCallbackIsInvalid(wrapper, folder);

            // Index writes leave their changes in the WAL (no checkpoint per call)
            var wal = Path.Combine(folder, ".ddb", "dbase.sqlite-wal");
            WalLength(wal).ShouldBeGreaterThan(0, "the index WAL before DDBShutdown");

            Ok(NativeDdb.DDBShutdown(30_000), "DDBShutdown");
            Step("DDBShutdown(30 s) returns DDBERR_NONE");
            Ok(NativeDdb.DDBShutdown(0), "DDBShutdown(again)");
            Step("DDBShutdown is idempotent");

            // The TRUNCATE checkpoint of the pooled connection emptied it
            WalLength(wal).ShouldBe(0, "the index WAL after DDBShutdown");
            Step("index WAL checkpointed (non-empty before, empty after)");

            // The exempt accessors still work
            DdbAbi.Decode(NativeDdb.DDBGetAbiVersion()).Major.ShouldBe(DdbAbi.Major);
            NativeDdb.DDBGetCapabilities().ShouldNotBe(IntPtr.Zero);
            wrapper.GetVersion().ShouldNotBeNullOrWhiteSpace();
            Ok(NativeDdb.DDBFreeCancelToken(IntPtr.Zero), "DDBFreeCancelToken(NULL)");
            Step("exempt accessors work after the shutdown");

            // Every other call is refused, DDBRegisterProcess and DDBConfigure too
            Fails(NativeDdb.DDBNormalizePath("a", null, out _), DdbResult.Shutdown, "DDBNormalizePath");
            Fails(NativeDdb.DDBRegisterProcess(false), DdbResult.Shutdown, "DDBRegisterProcess");
            Fails(NativeDdb.DDBConfigure("{}"), DdbResult.Shutdown, "DDBConfigure");
            Fails(NativeDdb.DDBListEntriesEx(folder, "", "", "", DdbListEntriesOptions.Create(true, false, false),
                out _), DdbResult.Shutdown, "DDBListEntriesEx");
            Should.Throw<DdbException>(() => wrapper.Init(Path.Combine(folder, "late")));
            Step("later calls return DDBERR_SHUTDOWN");

            Console.WriteLine(Passed);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex}");
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
            catch (IOException)
            {
                // A leftover temp folder does not change the result
            }
        }
    }

    /// <summary>DDBShutdown called from inside the log callback returns DDBERR_INVALIDARGS and shuts nothing down.</summary>
    private static void ShutdownFromTheLogCallbackIsInvalid(NativeDdbWrapper wrapper, string folder)
    {
        var notARaster = Path.Combine(folder, "not-a-raster.tif");
        File.WriteAllText(notARaster, "not a raster");

        Ok(NativeDdb.DDBSetLogCallback(&ShutdownOnLog, (int)DdbLogLevel.Warning, IntPtr.Zero), "DDBSetLogCallback");
        try
        {
            Should.Throw<DdbException>(() => wrapper.GetRasterInfo(notARaster));
        }
        finally
        {
            Ok(NativeDdb.DDBSetLogCallback(null, 0, IntPtr.Zero), "DDBSetLogCallback(NULL)");
        }

        ((DdbResult)Volatile.Read(ref _shutdownFromCallback)).ShouldBe(DdbResult.InvalidArgs,
            "DDBShutdown from the log callback");
        Ok(NativeDdb.DDBNormalizePath("a", null, out var output), "DDBNormalizePath after it");
        TakeString(output);
        Step("DDBShutdown from the log callback returns DDBERR_INVALIDARGS, the library keeps running");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ShutdownOnLog(int level, byte* message, byte* source, ulong opId, IntPtr userData)
    {
        // ddb.h: DDBShutdown called from the log callback returns DDBERR_INVALIDARGS. First record only.
        if (Volatile.Read(ref _shutdownFromCallback) == NotRun)
            Interlocked.CompareExchange(ref _shutdownFromCallback, (int)NativeDdb.DDBShutdown(0), NotRun);
    }

    private static long WalLength(string wal) => File.Exists(wal) ? new FileInfo(wal).Length : 0;

    private static void Step(string what) => Console.WriteLine($"ok: {what}");
}
