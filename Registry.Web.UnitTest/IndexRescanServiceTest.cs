using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Ports;
using Shouldly;
using DdbEntry = Registry.Ports.DroneDB.Entry;
using Registry.Web.Test.Support;

namespace Registry.Web.Test;

[TestFixture]
public class IndexRescanServiceTest : TestBase
{
    private Mock<IDdbManager> _ddbManagerMock = null!;
    private Mock<ISystemTaskSubmitter> _taskSubmitterMock = null!;
    private List<(string Org, string Ds, JsonElement Params, string? UserId)> _submitted = null!;
    private Dictionary<Guid, DdbEntry[]> _entries = null!;

    [SetUp]
    public void Setup()
    {
        _ddbManagerMock = new Mock<IDdbManager>();
        _taskSubmitterMock = new Mock<ISystemTaskSubmitter>();
        _submitted = [];
        _entries = [];

        _taskSubmitterMock
            .Setup(x => x.SubmitAsync(HeavyToolIds.RescanIndex, It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IDDB>(), null, It.IsAny<JsonElement>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IDDB, string?, JsonElement, string?, CancellationToken>(
                (_, org, ds, _, _, prms, userId, _) => _submitted.Add((org, ds, prms.Clone(), userId)))
            .ReturnsAsync(() => $"task-{_submitted.Count}");

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns<string, Guid>((_, internalRef) =>
            {
                if (!_entries.TryGetValue(internalRef, out var entries))
                    throw new DirectoryNotFoundException("Dataset folder is missing");
                var ddb = new Mock<IDDB>();
                // Returns every entry: SubmitAsync must still count only the matching ones
                ddb.Setup(d => d.FindSummaries(It.IsAny<EntryQuery>())).Returns(entries);
                return ddb.Object;
            });
    }

    [Test]
    public async Task SubmitAsync_SubmitsOneTaskPerDatasetWithMatches()
    {
        await using var context = CreateContext(
            ("org-a", "plans", [Entry("site.DWG", EntryType.Generic), Entry("ortho.tif", EntryType.GeoRaster)]),
            ("org-a", "photos", [Entry("img.jpg", EntryType.GeoImage)]),
            ("org-b", "cad", [Entry("x.dwg", EntryType.Generic), Entry("y.dwg", EntryType.Generic),
                Entry("dir.dwg", EntryType.Directory)]));

        await CreateService(context).SubmitAsync(null, null, null, "dwg", false, "admin-id");

        _submitted.Select(s => $"{s.Org}/{s.Ds}").ShouldBe(["org-a/plans", "org-b/cad"]);
        var prms = _submitted[0].Params;
        prms.GetProperty("types").ValueKind.ShouldBe(JsonValueKind.Null);
        prms.GetProperty("extensions").GetString().ShouldBe("dwg");
        prms.GetProperty("stopOnError").GetBoolean().ShouldBeFalse();
        _submitted.ShouldAllBe(s => s.UserId == "admin-id");
    }

    [Test]
    public async Task SubmitAsync_OrganizationScope_SkipsOtherOrganizations()
    {
        await using var context = CreateContext(
            ("org-a", "plans", [Entry("a.dxf", EntryType.Vector)]),
            ("org-b", "cad", [Entry("b.dxf", EntryType.Vector)]));

        await CreateService(context).SubmitAsync("org-b", null, "vector", "dxf", true, null);

        _submitted.Select(s => $"{s.Org}/{s.Ds}").ShouldBe(["org-b/cad"]);
        _submitted[0].Params.GetProperty("types").GetString().ShouldBe("vector");
        _submitted[0].Params.GetProperty("stopOnError").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task SubmitAsync_DatasetFailure_DoesNotStopTheOthers()
    {
        await using var context = CreateContext(
            ("org-a", "broken", [Entry("a.dwg", EntryType.Generic)]),
            ("org-a", "plans", [Entry("b.dwg", EntryType.Generic)]));
        _entries.Remove(context.Datasets.Single(d => d.Slug == "broken").InternalRef);

        await CreateService(context).SubmitAsync(null, null, null, "dwg", false, null);

        _submitted.Select(s => $"{s.Org}/{s.Ds}").ShouldBe(["org-a/plans"]);
    }

    [Test]
    public async Task SubmitAsync_NoFilter_Throws()
    {
        await using var context = CreateContext(("org-a", "plans", [Entry("a.dwg", EntryType.Generic)]));

        await Should.ThrowAsync<ArgumentException>(() =>
            CreateService(context).SubmitAsync(null, null, null, " ", false, null));
        _submitted.ShouldBeEmpty();
    }

    private IndexRescanService CreateService(RegistryContext context) =>
        new(context, _ddbManagerMock.Object, _taskSubmitterMock.Object, CreateTestLogger<IndexRescanService>());

    private static DdbEntry Entry(string path, EntryType type) => new() { Path = path, Type = type, Hash = path };

    private RegistryContext CreateContext(params (string Org, string Ds, DdbEntry[] Entries)[] datasets)
    {
        var context = TestDb.CreateContext<RegistryContext>();

        foreach (var group in datasets.GroupBy(d => d.Org))
        {
            var org = new Organization
            {
                Slug = group.Key, Name = group.Key, CreationDate = DateTime.UtcNow, OwnerId = "owner"
            };
            context.Organizations.Add(org);

            foreach (var (_, ds, entries) in group)
            {
                var dataset = new Dataset
                {
                    Slug = ds, InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow, Organization = org
                };
                context.Datasets.Add(dataset);
                _entries[dataset.InternalRef] = entries;
            }
        }

        context.SaveChanges();
        return context;
    }
}
