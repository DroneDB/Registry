#nullable enable
using System;
using System.IO;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Ports.DroneDB;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The daily dataset cleanup deletes push folders that were never committed and streamed
/// upload temp files, once nothing was written to them for <see cref="DatasetUploads.AbandonedAfter"/>.
/// </summary>
[TestFixture]
public class AbandonedUploadsTests
{
    private string _root = null!;
    private readonly DateTime _now = DateTime.UtcNow;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "abandoned-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, IDDB.DatabaseFolderName, IDDB.TmpFolderName));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    private string Tmp(string name) => Path.Combine(_root, IDDB.DatabaseFolderName, IDDB.TmpFolderName, name);

    private string File(string path, TimeSpan age)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "x");
        System.IO.File.SetLastWriteTimeUtc(path, _now - age);
        return path;
    }

    private string Folder(string path, TimeSpan age)
    {
        Directory.CreateDirectory(path);
        Directory.SetLastWriteTimeUtc(path, _now - age);
        return path;
    }

    [Test]
    public void RemovesOnlyAbandonedPushFoldersAndTempFiles()
    {
        var old = TimeSpan.FromDays(3);
        var recent = TimeSpan.FromMinutes(5);

        // Abandoned push: old session, old upload
        var abandoned = Tmp("push-" + Guid.NewGuid());
        File(Path.Combine(abandoned, "session.json"), old);
        File(Path.Combine(abandoned, "add", "a.jpg"), old);
        Folder(Path.Combine(abandoned, "add"), old);
        Folder(abandoned, old);

        // Push still uploading: old folder, but a file written a few minutes ago
        var active = Tmp("push-" + Guid.NewGuid());
        File(Path.Combine(active, "session.json"), old);
        File(Path.Combine(active, "add", "b.jpg"), recent);
        Folder(active, old);

        // Not push folders
        var other = Folder(Tmp("pull_cache"), old);

        var uploads = Path.Combine(_root, IDDB.UploadsFolderName);
        var oldTemp = File(Path.Combine(uploads, "1.tmp"), old);
        var newTemp = File(Path.Combine(uploads, "2.tmp"), recent);
        var quarantined = File(Path.Combine(uploads, IDDB.QuarantineFolderName, "3.tmp"), old);

        DatasetUploads.RemoveAbandoned(new FileSystem(), _root, _now, DatasetUploads.AbandonedAfter).ShouldBe(2);

        Directory.Exists(abandoned).ShouldBeFalse();
        Directory.Exists(active).ShouldBeTrue();
        Directory.Exists(other).ShouldBeTrue();
        System.IO.File.Exists(oldTemp).ShouldBeFalse();
        System.IO.File.Exists(newTemp).ShouldBeTrue();
        System.IO.File.Exists(quarantined).ShouldBeTrue("the quarantine belongs to the reconciliation sweep");
    }

    [Test]
    public void DatasetWithoutTempFolders_NothingToDo()
    {
        var empty = Path.Combine(_root, "empty-dataset");
        Directory.CreateDirectory(empty);

        DatasetUploads.RemoveAbandoned(new FileSystem(), empty, _now, DatasetUploads.AbandonedAfter).ShouldBe(0);
    }
}
