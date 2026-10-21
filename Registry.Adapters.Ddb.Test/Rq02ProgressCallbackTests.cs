using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Ports;
using Registry.Test.Common;
using Shouldly;

namespace Registry.Adapters.Ddb.Test;

/// <summary>
/// RQ-02 item 8: the progress callback handed to <c>DDBExportRaster2</c> called the managed
/// <c>progress</c> delegate without a try/catch, so an exception thrown by it unwound through the
/// native frames and ended the process. The bridge now catches it, asks the native call to cancel
/// (non-zero return) and rethrows the original exception once the P/Invoke has returned, ahead of
/// the <see cref="DdbCanceledException"/> the canceled result would raise.
/// Run: dotnet test Registry.Adapters.Ddb.Test --filter "FullyQualifiedName~Rq02ProgressCallbackTests"
/// </summary>
[TestFixture]
[Category("ImprovementBaseline")]
public class Rq02ProgressCallbackTests : TestBase
{
    private const string BaseTestFolder = nameof(Rq02ProgressCallbackTests);

    private const string TestGeoTiffUrl =
        "https://github.com/DroneDB/test_data/raw/master/brighton/odm_orthophoto.tif";

    private static readonly IDdbWrapper DdbWrapper = new NativeDdbWrapper(true);

    private string _outputFolder = null!;

    /// <summary>Thrown by the test callback; a type the wrapper never raises on its own.</summary>
    private sealed class ProgressObserverException() : Exception("progress observer failed");

    [OneTimeSetUp]
    public void Setup()
    {
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            Directory.SetCurrentDirectory(TestContext.CurrentContext.TestDirectory);

            var ddbFolder = CommonUtils.FindDdbFolder();
            if (ddbFolder == null)
                throw new Exception("DDB not found");

            CommonUtils.SetDefaultDllPath(ddbFolder);
        }

        DdbWrapper.RegisterProcess(true);
    }

    [SetUp]
    public void CreateOutputFolder()
    {
        _outputFolder = Path.Combine(Path.GetTempPath(), BaseTestFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outputFolder);
    }

    [TearDown]
    public void DeleteOutputFolder()
    {
        if (Directory.Exists(_outputFolder))
            Directory.Delete(_outputFolder, true);
    }

    /// <summary>Control: the export reports progress through the callback (what the fault test relies on).</summary>
    [Test]
    public void ExportRaster_ProgressCallback_IsInvoked()
    {
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);
        var output = Path.Combine(_outputFolder, "export.tif");
        var calls = 0;

        DdbWrapper.ExportRaster(tempFile.FilePath, output, null, null, null, null, null, null, 0,
            (_, _) => Interlocked.Increment(ref calls), CancellationToken.None);

        calls.ShouldBeGreaterThan(0);
        File.Exists(output).ShouldBeTrue();
    }

    /// <summary>
    /// Before the fix the exception crossed the native frames and aborted the test host; now the
    /// caller gets the original exception and the native call stops at the first report.
    /// </summary>
    [Test]
    public void ExportRaster_ProgressCallbackThrows_RethrowsTheOriginalExceptionAfterTheNativeCall()
    {
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);
        var output = Path.Combine(_outputFolder, "export.tif");
        var calls = 0;

        var thrown = Should.Throw<ProgressObserverException>(() =>
            DdbWrapper.ExportRaster(tempFile.FilePath, output, null, null, null, null, null, null, 0,
                (_, _) =>
                {
                    Interlocked.Increment(ref calls);
                    throw new ProgressObserverException();
                }, CancellationToken.None));

        thrown.StackTrace.ShouldNotBeNull();
        calls.ShouldBe(1, "the bridge cancels the native call and does not call the observer again");

        // The process survived: the native library still answers
        DdbWrapper.GetVersion().ShouldNotBeNullOrWhiteSpace();
    }
}
