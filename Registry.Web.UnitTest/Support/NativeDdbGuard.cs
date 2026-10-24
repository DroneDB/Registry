#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Registry.Adapters.DroneDB;

namespace Registry.Web.Test.Support;

/// <summary>
/// Keeps native libddb out of the unit test process (D5) and proves it stayed out:
/// <list type="bullet">
/// <item><see cref="Install"/> resolves the <c>[DllImport("ddb")]</c> bindings of Registry.Adapters
/// to a <see cref="DllNotFoundException"/> that names the test, as if libddb were not installed,
/// and records the attempt (an adapter that wraps the exception cannot hide it);</item>
/// <item><see cref="LoadedModules"/> lists the libddb modules mapped in the process
/// (<c>/proc/self/maps</c> on Linux, the process modules elsewhere), whatever loaded them.</item>
/// </list>
/// </summary>
public static partial class NativeDdbGuard
{
    /// <summary>The library name of the libddb bindings (<c>[DllImport("ddb")]</c>).</summary>
    public const string LibraryName = "ddb";

    private static readonly ConcurrentQueue<string> BlockedAttempts = new();

    /// <summary>The tests that tried to load libddb, with the library name they asked for.</summary>
    public static IReadOnlyCollection<string> Attempts => BlockedAttempts.ToArray();

    /// <summary>Blocks libddb for the bindings of Registry.Adapters (once per process).</summary>
    public static void Install() =>
        NativeLibrary.SetDllImportResolver(typeof(NativeDdbWrapper).Assembly, Resolve);

    /// <summary>The paths of the libddb modules loaded in this process (none in a unit test run).</summary>
    public static IReadOnlyList<string> LoadedModules()
    {
        IEnumerable<string> paths = OperatingSystem.IsLinux()
            // Columns: address perms offset dev inode [path]
            ? File.ReadLines("/proc/self/maps")
                .Select(l => l.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries))
                .Where(c => c.Length == 6)
                .Select(c => c[5].Trim())
            : Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Select(m => m.FileName);

        return paths.Where(p => DdbModule().IsMatch(Path.GetFileName(p))).Distinct().ToList();
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero; // default probing (libc, ucrtbase, ...)

        var test = TestContext.CurrentContext.Test.FullName;
        BlockedAttempts.Enqueue($"{test} (DllImport \"{libraryName}\")");
        throw new DllNotFoundException(
            $"Registry.Web.UnitTest never loads native libddb (D5), but {test} called it. Use FakeDdbWrapper " +
            "or a Mock<IDdbWrapper>, or move the test to Registry.Web.IntegrationTest.");
    }

    // libddb.so[.N...] (Linux), ddb.dll (Windows), libddb[.N].dylib (macOS)
    [GeneratedRegex(@"^(libddb\.so(\..*)?|ddb\.dll|libddb(\..*)?\.dylib)$", RegexOptions.IgnoreCase)]
    private static partial Regex DdbModule();
}
