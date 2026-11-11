using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Adapters;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>The whole-dataset archive carries the index with its committed changes.</summary>
[TestFixture]
public class ZipArchiveBuilderTests
{
    [Test]
    public async Task WholeDataset_CheckpointsTheIndex_AndSkipsOnlyTheBuildFolderAndTheWalIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), "zipbuilder-" + Guid.NewGuid().ToString("N"));
        var ddbFolder = Path.Combine(root, IDDB.DatabaseFolderName);
        Directory.CreateDirectory(Path.Combine(ddbFolder, Registry.Adapters.DroneDB.DDB.BuildFolderName, "hash"));
        Directory.CreateDirectory(Path.Combine(ddbFolder, "zz-after-build"));
        foreach (var name in new[] { IDDB.DatabaseFileName, IDDB.DatabaseFileName + "-wal", IDDB.DatabaseFileName + "-shm" })
            await File.WriteAllTextAsync(Path.Combine(ddbFolder, name), name);
        await File.WriteAllTextAsync(Path.Combine(ddbFolder, Registry.Adapters.DroneDB.DDB.BuildFolderName, "hash", "cog.tif"), "built");
        await File.WriteAllTextAsync(Path.Combine(ddbFolder, "zz-after-build", "meta.json"), "{}");

        var ddb = new Mock<IDDB>();
        ddb.SetupGet(d => d.DatasetFolderPath).Returns(root);
        ddb.SetupGet(d => d.DerivedFolderPaths).Returns([Path.Combine(ddbFolder, Registry.Adapters.DroneDB.DDB.BuildFolderName)]);
        ddb.Setup(d => d.Checkpoint()).Returns(true);

        try
        {
            using var output = new MemoryStream();
            await new ZipArchiveBuilder().WriteZipAsync(ddb.Object, [], [], includeDdb: true, output);

            output.Position = 0;
            using var archive = new ZipArchive(output, ZipArchiveMode.Read);
            var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToArray();

            ddb.Verify(d => d.Checkpoint(), Times.Once);
            names.ShouldContain(".ddb/" + IDDB.DatabaseFileName);
            names.ShouldContain(".ddb/" + IDDB.DatabaseFileName + "-wal");
            names.ShouldContain(".ddb/zz-after-build/meta.json"); // an exclusion does not stop the walk
            names.ShouldNotContain(".ddb/" + IDDB.DatabaseFileName + "-shm");
            names.ShouldNotContain(n => n.Contains("/" + Registry.Adapters.DroneDB.DDB.BuildFolderName + "/"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
