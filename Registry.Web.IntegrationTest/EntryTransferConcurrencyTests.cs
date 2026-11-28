using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// On the real libddb and the real file system: identical transfers sent at the same moment never lose the entry.
/// Nothing reserved the destination, so both requests passed the existence check and copied; the one that came
/// second failed to remove the source the first had removed and its rollback deleted the destination the first had
/// written: the file was gone from both datasets.
/// Run: dotnet test Registry.Web.IntegrationTest --filter "FullyQualifiedName~EntryTransferConcurrencyTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Native)]
public class EntryTransferConcurrencyTests
{
    private const string Org = "org";
    private const int Rounds = 40;

    private readonly NativeDdbWrapper _wrapper = new();
    private string _root = null!;
    private TestDb<RegistryContext> _db = null!;
    private DdbManager _ddbManager = null!;
    private DDB _source = null!;
    private DDB _destination = null!;
    private Mock<IDatasetResolver> _datasets = null!;

    [SetUp]
    public async Task SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "transferrace-" + Guid.NewGuid().ToString("N"));
        var sourceRef = Guid.NewGuid();
        var destinationRef = Guid.NewGuid();
        _source = NewDataset(Path.Combine(_root, Org, sourceRef.ToString()));
        _destination = NewDataset(Path.Combine(_root, Org, destinationRef.ToString()));

        // A file database: every request has its own context and connection, as in the web host
        _db = TestDb.CreateFile<RegistryContext>();
        await using (var context = _db.NewContext())
        {
            var org = Seed.Organization(context, Org);
            var sourceDs = Seed.Dataset(context, org, "src", sourceRef);
            var destinationDs = Seed.Dataset(context, org, "dst", destinationRef);
            await context.SaveChangesAsync();

            _datasets = new Mock<IDatasetResolver>();
            foreach (var dataset in new[] { sourceDs, destinationDs })
                _datasets.Setup(u => u.GetDatasetAsync(Org, dataset.Slug, It.IsAny<bool>(), It.IsAny<bool>(),
                    It.IsAny<CancellationToken>())).ReturnsAsync(dataset);
        }

        _ddbManager = new DdbManager(TestSettings.Options<StorageOptions>(Settings()),
            NullLogger<DdbManager>.Instance, _wrapper);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var folder in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)
                     .Where(d => File.Exists(Path.Combine(d, ".ddb", "dbase.sqlite"))))
            _wrapper.ReleaseConnections(folder);
        _db.Dispose();
        Directory.Delete(_root, true);
    }

    private AppSettings Settings() => new() { DatasetsPath = _root, EnableStorageLimiter = false };

    private DDB NewDataset(string folder)
    {
        Directory.CreateDirectory(folder);
        var ddb = new DDB(folder, _wrapper);
        ddb.Init();
        return ddb;
    }

    // What one request has: its own context and services
    private async Task TransferAsync(bool overwrite, bool keepSource, string path)
    {
        await using var context = _db.NewContext();
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>())).ReturnsAsync(true);
        var transfers = new ObjectServicesBuilder(context)
            .WithSettings(TestSettings.Options(Settings()))
            .WithDdbManager(_ddbManager)
            .WithDatasets(_datasets.Object)
            .WithAuthManager(auth.Object)
            .WithDdbWrapper(_wrapper)
            .WithBuildArtifactResolver(TestPorts.Artifacts(_wrapper))
            .Build().Transfers;

        await transfers.TransferAsync(Org, "src", path, Org, "dst", path, overwrite, keepSource);
    }

    private static byte[] Content(int seed)
    {
        var bytes = new byte[512 * 1024];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static void Add(DDB ddb, string path, byte[] content)
    {
        File.WriteAllBytes(Path.Combine(ddb.DatasetFolderPath, path), content);
        ddb.AddRaw(path);
    }

    // Both requests start together; the outcomes are kept, none is thrown
    private static async Task<Exception?[]> RunTogetherAsync(Func<Task> request)
    {
        var start = new TaskCompletionSource();
        var requests = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                await request();
                return (Exception?)null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        })).ToArray();

        start.SetResult();
        return await Task.WhenAll(requests);
    }

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    [Test]
    public async Task IdenticalMovesWithOverwrite_NeverLoseTheFile([Values] bool destinationExists)
    {
        for (var round = 0; round < Rounds; round++)
        {
            var path = $"moved-{round}.bin";
            var content = Content(round);
            Add(_source, path, content);
            if (destinationExists)
                Add(_destination, path, Content(1000 + round));

            var outcomes = await RunTogetherAsync(() => TransferAsync(overwrite: true, keepSource: false, path));

            // One request moved it; the other found the source gone (404), whatever order they ran in
            outcomes.Count(o => o == null).ShouldBe(1, $"round {round}: {string.Join("; ", outcomes.Select(o => o?.Message))}");
            outcomes.Single(o => o != null).ShouldBeOfType<NotFoundException>($"round {round}");

            var local = Path.Combine(_destination.DatasetFolderPath, path);
            File.Exists(local).ShouldBeTrue($"round {round}: the file is gone from the destination");
            Hash(File.ReadAllBytes(local)).ShouldBe(Hash(content), $"round {round}");
            _destination.GetEntry(path).ShouldNotBeNull($"round {round}: the destination index lost the entry");
            File.Exists(Path.Combine(_source.DatasetFolderPath, path)).ShouldBeFalse($"round {round}");
            _source.GetEntry(path).ShouldBeNull($"round {round}");
            LeftOvers().ShouldBeEmpty($"round {round}");
        }
    }

    [Test]
    public async Task IdenticalMovesWithoutOverwrite_NeverLoseTheFile()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var path = $"plain-{round}.bin";
            var content = Content(2000 + round);
            Add(_source, path, content);

            var outcomes = await RunTogetherAsync(() => TransferAsync(overwrite: false, keepSource: false, path));

            outcomes.Count(o => o == null).ShouldBe(1, $"round {round}: {string.Join("; ", outcomes.Select(o => o?.Message))}");
            outcomes.Single(o => o != null).ShouldBeOfType<NotFoundException>($"round {round}");
            Hash(File.ReadAllBytes(Path.Combine(_destination.DatasetFolderPath, path))).ShouldBe(Hash(content), $"round {round}");
            _destination.GetEntry(path).ShouldNotBeNull($"round {round}");
            _source.GetEntry(path).ShouldBeNull($"round {round}");
        }
    }

    // A copy leaves the source: both requests may succeed, and the destination is one whole file
    [Test]
    public async Task IdenticalCopiesWithOverwrite_LeaveOneWholeFile()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var path = $"copied-{round}.bin";
            var content = Content(3000 + round);
            Add(_source, path, content);
            Add(_destination, path, Content(4000 + round));

            var outcomes = await RunTogetherAsync(() => TransferAsync(overwrite: true, keepSource: true, path));

            outcomes.ShouldAllBe(o => o == null, $"round {round}: {string.Join("; ", outcomes.Select(o => o?.Message))}");
            Hash(File.ReadAllBytes(Path.Combine(_destination.DatasetFolderPath, path))).ShouldBe(Hash(content), $"round {round}");
            _destination.GetEntry(path).ShouldNotBeNull($"round {round}");
            Hash(File.ReadAllBytes(Path.Combine(_source.DatasetFolderPath, path))).ShouldBe(Hash(content), $"round {round}");
            _source.GetEntry(path).ShouldNotBeNull($"round {round}");
            LeftOvers().ShouldBeEmpty($"round {round}");
        }
    }

    // The temporary and trash siblings of a placement never outlive it
    private List<string> LeftOvers() =>
        Directory.EnumerateFiles(_destination.DatasetFolderPath, "*", SearchOption.TopDirectoryOnly)
            .Where(f => f.Contains(".copying-") || f.Contains(".trash-")).ToList();
}
