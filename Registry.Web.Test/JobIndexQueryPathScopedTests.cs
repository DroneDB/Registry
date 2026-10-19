using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.Adapters;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// REG-02: path-scoped job index queries used by the build badges of a listing.
/// </summary>
[TestFixture]
public class JobIndexQueryPathScopedTests
{
    private RegistryContext _context;
    private JobIndexQuery _query;

    [SetUp]
    public void Setup()
    {
        _context = new RegistryContext(new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase("JobIndexPaths-" + Guid.NewGuid())
            .Options);
        _query = new JobIndexQuery(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private void Add(string id, string path, string state, int minutesAgo = 0, string tool = "build",
        string ds = "ds")
    {
        _context.JobIndices.Add(new JobIndex
        {
            JobId = id, OrgSlug = "org", DsSlug = ds, Path = path, ToolId = tool, CurrentState = state,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-minutesAgo)
        });
    }

    [Test]
    public async Task GetActivePaths_SpansChunksAndFiltersToolDatasetAndState()
    {
        var paths = Enumerable.Range(0, 1200).Select(i => $"p{i}.tif").ToArray();
        Add("a", "p1.tif", "Processing");
        Add("b", "p700.tif", "Enqueued");
        Add("c", "p1100.tif", "Created");
        Add("d", "p2.tif", "Succeeded");
        Add("e", "p3.tif", "Processing", tool: "raster-export");
        Add("f", "p4.tif", "Processing", ds: "other");
        Add("g", "unlisted.tif", "Processing");
        await _context.SaveChangesAsync();

        var active = await _query.GetActivePathsAsync("org", "ds", "build", paths);

        active.OrderBy(p => p).ShouldBe(["p1.tif", "p1100.tif", "p700.tif"]);
    }

    [Test]
    public async Task GetLatestStatesByPath_PicksMostRecentJobPerPath()
    {
        Add("a1", "a.tif", "Failed", minutesAgo: 10);
        Add("a2", "a.tif", "Succeeded", minutesAgo: 5);
        Add("b1", "b.tif", "Succeeded", minutesAgo: 10);
        Add("b2", "b.tif", "Failed", minutesAgo: 1);
        Add("c1", "c.tif", "Failed", tool: "raster-export");
        Add("d1", "d.tif", "Failed", ds: "other");
        await _context.SaveChangesAsync();

        var latest = await _query.GetLatestStatesByPathAsync("org", "ds", "build",
            ["a.tif", "b.tif", "c.tif", "d.tif", "a.tif"]);

        latest.Count.ShouldBe(2);
        latest["a.tif"].ShouldBe("Succeeded");
        latest["b.tif"].ShouldBe("Failed");
    }
}
