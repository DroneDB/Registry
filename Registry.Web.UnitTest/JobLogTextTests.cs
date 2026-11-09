using System;
using System.IO;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Services.Jobs;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <see cref="JobLogText.UserMessage(Exception?, string?)"/> (FR-XC-06, SEC-001, FR-PRC-24, FR-PRC-16): the reason a
/// task failed, as any reader of the task and the build-failure e-mail see it. libddb-like messages lose every server
/// path (the dataset folder, its build folder with the hash, any other absolute path), known GDAL messages become
/// plain text, an unsafe archive entry is named, and the text fits the column.
/// </summary>
[TestFixture]
public class JobLogTextTests
{
    private const string UnixFolder = "/mnt/data/datasets/org/0b9c4a52-3f5e-4c1e-9a65-1f0e2d3c4b5a";
    private const string WindowsFolder = @"C:\data\datasets\org\0b9c4a52-3f5e-4c1e-9a65-1f0e2d3c4b5a";
    private const string BuildHash = "7a9f3c2e1d0b4a5968778695a4b3c2d1e0f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5";

    [TestCase(UnixFolder)]
    [TestCase(null)]
    public void ABuildFolderPath_LosesTheServerFolderAndTheHash(string? folder)
    {
        var message = JobLogText.UserMessage(
            new DdbException($"GDAL error: {UnixFolder}/.ddb/build/{BuildHash}/ept/ept.json: cannot open"), folder);

        message.ShouldBe("GDAL error: [build folder]: cannot open");
        message.ShouldNotContain("/mnt/");
        message.ShouldNotContain("0b9c4a52");
        message.ShouldNotContain(BuildHash);
    }

    [Test]
    public void AFileOfTheDataset_IsNamedRelativeToIt()
    {
        JobLogText.UserMessage(new InvalidOperationException("Cannot build 'a.tif'",
                new DdbException($"Cannot open {UnixFolder}/images/a.tif: no such file")), UnixFolder)
            .ShouldBe("Cannot open [dataset]/images/a.tif: no such file");

        var windows = JobLogText.UserMessage(new DdbException($@"Cannot read {WindowsFolder}\images\a.tif"), WindowsFolder);
        windows.ShouldBe(@"Cannot read [dataset]\images\a.tif");
        windows.ShouldNotContain(@"C:\");
    }

    [Test]
    public void TheFullPathFormOfTheFolder_IsTheDatasetToo()
    {
        var full = Path.Combine(Path.GetTempPath(), "ddb-user-message", "ds");
        var given = Path.Combine(Path.GetTempPath(), "ddb-user-message", "other", "..", "ds");

        JobLogText.UserMessage($"Cannot open {Path.Combine(full, "a.tif")}: not a raster", given)
            .ShouldBe($"Cannot open {JobLogText.DatasetPlaceholder}{Path.DirectorySeparatorChar}a.tif: not a raster");
    }

    [Test]
    public void AnyOtherAbsolutePath_IsAServerPath()
    {
        JobLogText.UserMessage(@"Temp file C:\Users\x\AppData\Local\Temp\ddb-1\a.tif is missing.", UnixFolder)
            .ShouldBe("Temp file [server path] is missing.");
        JobLogText.UserMessage("Cannot write /var/lib/registry/tmp/ddb-1/a.tif.", null)
            .ShouldBe("Cannot write [server path].");
        JobLogText.UserMessage(@"Share \\server\share\datasets\a.tif is offline", null)
            .ShouldBe("Share [server path] is offline");
    }

    // A quoted path runs to its closing quote: a folder name may have spaces
    [Test]
    public void AQuotedPathWithSpaces_IsReplacedWhole()
    {
        JobLogText.UserMessage(@"Cannot open 'C:\Users\Jane Doe\reg\x.tif': access denied", null)
            .ShouldBe("Cannot open '[server path]': access denied");
        JobLogText.UserMessage("Cannot open \"/srv/my data/a b.tif\" now", null)
            .ShouldBe("Cannot open \"[server path]\" now");
        JobLogText.UserMessage("Cannot build 'a b.tif' from '/tmp'", null)
            .ShouldBe("Cannot build 'a b.tif' from '/tmp'");
    }

    [Test]
    public void ADrivePathAfterAColon_IsReplaced() =>
        JobLogText.UserMessage(@"error:C:\srv\x.tif", null).ShouldBe("error:[server path]");

    [Test]
    public void AFileUri_IsReplaced()
    {
        JobLogText.UserMessage("Cannot read file:///srv/data/a.tif here", null).ShouldBe("Cannot read [server path] here");
        JobLogText.UserMessage("Cannot read file:///C:/srv/a.tif", null).ShouldBe("Cannot read [server path]");
    }

    [TestCase("Requires bands: RN")]
    [TestCase("Cannot reach https://hub.dronedb.app/orgs/x/ds/y")]
    [TestCase("Rescale 1/2 is not valid")]
    [TestCase("Cannot open [dataset]/images/a.tif")]
    [TestCase("Nothing in /tmp")]
    [TestCase("Not a coordinate system: EPSG:4326/x")]
    [TestCase("Cannot reach 'https://hub.dronedb.app/orgs/x'")]
    [TestCase("Cannot open '[dataset]/my images/a.tif'")]
    [TestCase("Time 12:30/14:00")]
    public void TextWithoutAServerPath_IsKept(string text) =>
        JobLogText.UserMessage(text, UnixFolder).ShouldBe(text);

    [TestCase("gdal_translate failed to create output COG: " + UnixFolder + "/.ddb/build/" + BuildHash + "/cog/cog.tif")]
    [TestCase("'" + UnixFolder + "/ortho.tif' not recognized as a supported file format.")]
    public void KnownRasterMessages_AreSaidInPlainWords(string native)
    {
        JobLogText.UserMessage(new InvalidOperationException("Cannot build 'ortho.tif'", new DdbException(native)),
            UnixFolder).ShouldBe("The file is damaged or not a valid GeoTIFF");
        JobLogText.UserMessage(native, null).ShouldBe(JobLogText.DamagedRasterMessage);
    }

    [Test]
    public void AnUnsafeArchiveEntry_IsNamed_AndNothingWasExtracted()
    {
        var ex = new InvalidDatasetPathException("../escape.txt", EntryPathRejection.Traversal, "archive entry");

        JobLogText.UserMessage(new InvalidOperationException("extraction failed", ex), UnixFolder)
            .ShouldBe("The archive contains an unsafe path: ../escape.txt. Nothing was extracted.");
        // An absolute entry is the archive's, not a path of the server
        JobLogText.UserMessage(new InvalidDatasetPathException("/etc/passwd", EntryPathRejection.Absolute,
                "archive entry"), UnixFolder)
            .ShouldBe("The archive contains an unsafe path: /etc/passwd. Nothing was extracted.");
        JobLogText.UserMessage(new InvalidDatasetPathException("a\0b", EntryPathRejection.Nul, "archive entry"), null)
            .ShouldBe("The archive contains an unsafe path: a\\u0000b. Nothing was extracted.");
    }

    [Test]
    public void AnotherInvalidPath_KeepsItsOwnMessage() =>
        JobLogText.UserMessage(new InvalidDatasetPathException("../a.tif", EntryPathRejection.Traversal, "output path"),
            UnixFolder).ShouldBe("Invalid output path '../a.tif': path traversal is not allowed.");

    [Test]
    public void TheInternalPrefixOfLibddb_IsRemoved() =>
        JobLogText.UserMessage(new DdbException("(internal) Something went wrong"), null)
            .ShouldBe("Something went wrong");

    [Test]
    public void ALongMessage_FitsTheColumn()
    {
        var message = JobLogText.UserMessage(new DdbException(new string('x', 5000)), null);

        message.Length.ShouldBe(JobLogText.UserMessageMaxLength);
        message.ShouldEndWith("...");
    }

    [Test]
    public void ABlankMessage_IsUnknown()
    {
        JobLogText.UserMessage((Exception?)null, UnixFolder).ShouldBe("Unknown error");
        JobLogText.UserMessage(new InvalidOperationException(" "), UnixFolder).ShouldBe("Unknown error");
        JobLogText.UserMessage("  ", UnixFolder).ShouldBe("Unknown error");
    }

    [Test]
    public void AMultiLineMessage_IsOneLine() =>
        JobLogText.UserMessage("first\r\nsecond\nthird", null).ShouldBe("first second third");
}
