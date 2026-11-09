using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Shouldly;

namespace Registry.Adapters.Test.DroneDB;

/// <summary>
/// The native declarations match ddb.h's binary layout (its static assertions) and the
/// marshalling rules, checked by reflection without loading libddb.
/// </summary>
[TestFixture]
public class NativeDdbDeclarationTests
{
    private static readonly MethodInfo[] Imports = typeof(NativeDdb)
        .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        .Where(m => m.GetCustomAttribute<LibraryImportAttribute>() != null)
        .ToArray();

    [Test]
    public void DdbAddOptions_Is8Bytes_LikeDDBAddOptions()
    {
        Unsafe.SizeOf<DdbAddOptions>().ShouldBe(8);
        Marshal.OffsetOf<DdbAddOptions>(nameof(DdbAddOptions.Recursive)).ShouldBe(0);
        Marshal.OffsetOf<DdbAddOptions>(nameof(DdbAddOptions.StopOnError)).ShouldBe(1);
        Marshal.OffsetOf<DdbAddOptions>(nameof(DdbAddOptions.MaxConflictRetries)).ShouldBe(4);
    }

    [Test]
    public void DdbOverwriteOptions_Is8Bytes_LikeTheAlignMergeAndMaskOptions()
    {
        Unsafe.SizeOf<DdbOverwriteOptions>().ShouldBe(8);
        Marshal.OffsetOf<DdbOverwriteOptions>(nameof(DdbOverwriteOptions.StructSize)).ShouldBe(0);
        Marshal.OffsetOf<DdbOverwriteOptions>(nameof(DdbOverwriteOptions.Overwrite)).ShouldBe(4);
        Marshal.OffsetOf<DdbOverwriteOptions>(nameof(DdbOverwriteOptions.Reserved0)).ShouldBe(5);
        Marshal.OffsetOf<DdbOverwriteOptions>(nameof(DdbOverwriteOptions.Reserved2)).ShouldBe(7);

        var replace = DdbOverwriteOptions.Create(true);
        (replace.StructSize, replace.Overwrite, replace.Reserved0, replace.Reserved1, replace.Reserved2)
            .ShouldBe((8u, (byte)1, (byte)0, (byte)0, (byte)0));
        DdbOverwriteOptions.Create(false).Overwrite.ShouldBe((byte)0);
    }

    [Test]
    public void DdbAddHint_Is32Bytes_LikeDDBAddHint()
    {
        Unsafe.SizeOf<DdbAddHint>().ShouldBe(32);
        Marshal.OffsetOf<DdbAddHint>(nameof(DdbAddHint.Sha256)).ShouldBe(8);
        Marshal.OffsetOf<DdbAddHint>(nameof(DdbAddHint.Size)).ShouldBe(16);
        Marshal.OffsetOf<DdbAddHint>(nameof(DdbAddHint.Mtime)).ShouldBe(24);
    }

    /// <summary>C's bool is 1 byte: a .NET bool marshalled as the 4-byte BOOL works only by chance.</summary>
    [Test]
    public void EveryBoolParameter_IsMarshalledAsOneByte()
    {
        var wrong = Imports
            .SelectMany(m => m.GetParameters().Select(p => (Method: m, Parameter: p)))
            .Where(x => (x.Parameter.ParameterType == typeof(bool) || x.Parameter.ParameterType == typeof(bool).MakeByRefType())
                        && x.Parameter.GetCustomAttribute<MarshalAsAttribute>()?.Value != UnmanagedType.U1)
            .Select(x => $"{x.Method.Name}({x.Parameter.Name})")
            .ToArray();

        wrong.ShouldBeEmpty();
    }

    /// <summary>One native-methods home: every import is a source-generated LibraryImport named after its export.</summary>
    [Test]
    public void RequiredSymbols_AreTheImports()
    {
        DdbAbi.RequiredSymbols.ShouldBe(Imports.Select(m => m.Name).Order(StringComparer.Ordinal).ToArray());
        DdbAbi.RequiredSymbols.ShouldContain("DDBGetAbiVersion");
        DdbAbi.RequiredSymbols.ShouldContain("DDBShutdown");
        // The splat exports are imports like the rest: a libddb without them fails the startup handshake
        DdbAbi.RequiredSymbols.ShouldContain("DDBExportSplatSpz");
        DdbAbi.RequiredSymbols.ShouldContain("DDBExportSplatTrainingData");
        // Align, merge and mask write through the 2.1 variants that take overwrite (FR-PRC-01), never the old ones
        DdbAbi.RequiredSymbols.ShouldContain("DDBAlignRasterEx");
        DdbAbi.RequiredSymbols.ShouldContain("DDBMergeMultispectralEx");
        DdbAbi.RequiredSymbols.ShouldContain("DDBMaskBordersEx");
        DdbAbi.RequiredSymbols.ShouldNotContain("DDBAlignRaster");
        DdbAbi.RequiredSymbols.ShouldNotContain("DDBMergeMultispectral");
        DdbAbi.RequiredSymbols.ShouldNotContain("DDBMaskBorders");
        DdbAbi.RequiredSymbols.ShouldAllBe(s => s.StartsWith("DDB"));
        Imports.ShouldAllBe(m => m.GetCustomAttribute<LibraryImportAttribute>()!.EntryPoint == null);
    }

    [Test]
    public void NoClassOfTheAdapter_DeclaresADllImport()
    {
        var dllImports = typeof(NativeDdb).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public |
                                          BindingFlags.DeclaredOnly))
            // The source generator's output: a compiler-generated local stub, or for a blittable
            // signature the LibraryImport method itself
            .Where(m => m.GetCustomAttribute<DllImportAttribute>() != null && !m.Name.Contains('<') &&
                        m.GetCustomAttribute<LibraryImportAttribute>() == null)
            .Where(m => m.GetCustomAttribute<DllImportAttribute>()!.Value is "ddb")
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToArray();

        dllImports.ShouldBeEmpty();
    }

    [Test]
    public void BuildJson_SendsOnlyTheLimitsThatAreSet()
    {
        NativeDdbWrapper.ToBuildJson(null).ShouldBeNull();
        NativeDdbWrapper.ToBuildJson(new Ports.DroneDB.BuildOptions()).ShouldBeNull();
        NativeDdbWrapper.ToBuildJson(Ports.DroneDB.BuildOptions.Capped(2)).ShouldBe("""{"threads":2}""");
    }

    [Test]
    public void TrainingJson_SendsOnlyTheOptionsThatAreSet()
    {
        NativeDdbWrapper.ToTrainingJson(null).ShouldBeNull();
        NativeDdbWrapper.ToTrainingJson(new Ports.DroneDB.SplatTrainingOptions()).ShouldBeNull();
        NativeDdbWrapper.ToTrainingJson(new Ports.DroneDB.SplatTrainingOptions(MaxPoints: 0)).ShouldBe("""{"maxPoints":0}""");
        NativeDdbWrapper.ToTrainingJson(new Ports.DroneDB.SplatTrainingOptions(1600, 50000))
            .ShouldBe("""{"maxImageSize":1600,"maxPoints":50000}""");
    }

    [Test]
    public void ConfigureJson_SendsOnlyTheSettingsThatAreSet()
    {
        NativeDdbRuntime.ToConfigureJson(new Ports.DroneDB.DdbProcessSettings()).ShouldBe("{}");
        NativeDdbRuntime.ToConfigureJson(new Ports.DroneDB.DdbProcessSettings(3, 2, 256, "/data/temp/ddb-cache"))
            .ShouldBe("""{"cpuPoolSize":3,"ioThreads":2,"minFreeDiskMb":256,"cacheDir":"/data/temp/ddb-cache"}""");
        NativeDdbRuntime.ToConfigureJson(new Ports.DroneDB.DdbProcessSettings(GdalCacheMb: 512))
            .ShouldBe("""{"gdalCacheMb":512}""");
        NativeDdbRuntime.ToConfigureJson(new Ports.DroneDB.DdbProcessSettings(IoThreads: 8)).ShouldBe("""{"ioThreads":8}""");
    }
}
