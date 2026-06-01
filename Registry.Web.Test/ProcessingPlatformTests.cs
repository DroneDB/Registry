using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class ProcessingPlatformTests
{
    #region HeavyToolRegistry

    private sealed class FakeTool : IHeavyTool
    {
        public FakeTool(string id, string version, HeavyToolPermission access = HeavyToolPermission.Read,
            bool producesArtifact = false)
        {
            Id = id;
            Version = version;
            RequiredAccess = access;
            ProducesArtifact = producesArtifact;
        }

        public string Id { get; }
        public string Version { get; }
        public string Title => Id;
        public HeavyToolPermission RequiredAccess { get; }
        public bool ProducesArtifact { get; }
        public JsonDocument InputSchema => JsonDocument.Parse("{}");

        public Task ValidateAsync(HeavyToolRequest request, IHeavyToolValidationContext ctx, CancellationToken ct)
            => Task.CompletedTask;

        public HeavyToolPlan Plan(HeavyToolRequest request, IHeavyToolValidationContext ctx)
            => new(null, "test", null, null);

        public Task<HeavyToolArtifact?> ExecuteAsync(HeavyToolRequest request, IHeavyToolExecutionContext ctx,
            IProgress<HeavyToolProgress> progress, CancellationToken ct)
            => Task.FromResult<HeavyToolArtifact?>(null);
    }

    [Test]
    public void Registry_Resolve_ByExactVersion_ReturnsMatch()
    {
        var registry = new HeavyToolRegistry(new IHeavyTool[]
        {
            new FakeTool("build", "1"),
            new FakeTool("build", "2")
        });

        registry.Resolve("build", "1")!.Version.ShouldBe("1");
        registry.Resolve("build", "2")!.Version.ShouldBe("2");
    }

    [Test]
    public void Registry_Resolve_WithoutVersion_PicksHighest()
    {
        var registry = new HeavyToolRegistry(new IHeavyTool[]
        {
            new FakeTool("build", "1"),
            new FakeTool("build", "2"),
            new FakeTool("build", "10")
        });

        registry.Resolve("build")!.Version.ShouldBe("10");
    }

    [Test]
    public void Registry_Resolve_UnknownTool_ReturnsNull()
    {
        var registry = new HeavyToolRegistry(new IHeavyTool[] { new FakeTool("build", "1") });

        registry.Resolve("does-not-exist").ShouldBeNull();
        registry.Resolve("build", "99").ShouldBeNull();
    }

    [Test]
    public void Registry_Resolve_IsCaseInsensitive()
    {
        var registry = new HeavyToolRegistry(new IHeavyTool[] { new FakeTool("raster-export", "1") });

        registry.Resolve("RASTER-EXPORT")!.Id.ShouldBe("raster-export");
    }

    [Test]
    public void Registry_DuplicateRegistration_Throws()
    {
        Should.Throw<InvalidOperationException>(() => new HeavyToolRegistry(new IHeavyTool[]
        {
            new FakeTool("build", "1"),
            new FakeTool("build", "1")
        }));
    }

    [Test]
    public void Registry_All_ExposesEveryTool()
    {
        var registry = new HeavyToolRegistry(new IHeavyTool[]
        {
            new FakeTool("build", "1"),
            new FakeTool("raster-export", "1")
        });

        registry.All.Count.ShouldBe(2);
    }

    #endregion

    #region JobIndexQuery

    private static RegistryContext NewContext(params JobIndex[] seed)
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase("ProcPlatformTestDb_" + Guid.NewGuid())
            .Options;

        using (var ctx = new RegistryContext(options))
        {
            if (seed.Length > 0)
            {
                ctx.JobIndices.AddRange(seed);
                ctx.SaveChanges();
            }
        }

        return new RegistryContext(options);
    }

    private static JobIndex Job(string id, string state, string toolId = "build",
        string? userId = null, string? requestHash = null, string org = "org1", string ds = "ds1",
        DateTime? created = null)
        => new()
        {
            JobId = id,
            OrgSlug = org,
            DsSlug = ds,
            ToolId = toolId,
            ToolVersion = "1",
            CurrentState = state,
            UserId = userId,
            RequestHash = requestHash,
            CreatedAtUtc = created ?? DateTime.UtcNow,
            LastStateChangeUtc = created ?? DateTime.UtcNow
        };

    [Test]
    public async Task QueryAsync_FiltersByToolAndState()
    {
        await using var ctx = NewContext(
            Job("a", "Processing", "build"),
            Job("b", "Succeeded", "build"),
            Job("c", "Processing", "raster-export"));
        var query = new JobIndexQuery(ctx);

        var result = await query.QueryAsync(new JobIndexQueryFilter("org1", "ds1",
            ToolId: "build", State: "Processing"));

        result.Length.ShouldBe(1);
        result[0].JobId.ShouldBe("a");
    }

    [Test]
    public async Task QueryAsync_FiltersByUser()
    {
        await using var ctx = NewContext(
            Job("a", "Processing", userId: "u1"),
            Job("b", "Processing", userId: "u2"));
        var query = new JobIndexQuery(ctx);

        var result = await query.QueryAsync(new JobIndexQueryFilter("org1", "ds1", UserId: "u1"));

        result.Length.ShouldBe(1);
        result[0].JobId.ShouldBe("a");
    }

    [Test]
    public async Task CountActiveAsync_CountsOnlyActiveStates()
    {
        await using var ctx = NewContext(
            Job("a", "Created"),
            Job("b", "Enqueued"),
            Job("c", "Scheduled"),
            Job("d", "Processing"),
            Job("e", "Succeeded"),
            Job("f", "Failed"));
        var query = new JobIndexQuery(ctx);

        (await query.CountActiveAsync()).ShouldBe(4);
    }

    [Test]
    public async Task CountActiveAsync_ScopesByUser()
    {
        await using var ctx = NewContext(
            Job("a", "Processing", userId: "u1"),
            Job("b", "Processing", userId: "u1"),
            Job("c", "Processing", userId: "u2"));
        var query = new JobIndexQuery(ctx);

        (await query.CountActiveAsync(userId: "u1")).ShouldBe(2);
    }

    [Test]
    public async Task FindDedupCandidate_ReturnsActiveMatch()
    {
        await using var ctx = NewContext(
            Job("a", "Processing", requestHash: "hash-x"));
        var query = new JobIndexQuery(ctx);

        var found = await query.FindDedupCandidateAsync("org1", "ds1", "build", "hash-x", 24);

        found.ShouldNotBeNull();
        found!.JobId.ShouldBe("a");
    }

    [Test]
    public async Task FindDedupCandidate_RecentSucceeded_WithinLookback_Matches()
    {
        await using var ctx = NewContext(
            Job("a", "Succeeded", requestHash: "hash-x", created: DateTime.UtcNow.AddHours(-1)));
        var query = new JobIndexQuery(ctx);

        var found = await query.FindDedupCandidateAsync("org1", "ds1", "build", "hash-x", 24);

        found.ShouldNotBeNull();
    }

    [Test]
    public async Task FindDedupCandidate_OldSucceeded_OutsideLookback_NoMatch()
    {
        await using var ctx = NewContext(
            Job("a", "Succeeded", requestHash: "hash-x", created: DateTime.UtcNow.AddHours(-48)));
        var query = new JobIndexQuery(ctx);

        var found = await query.FindDedupCandidateAsync("org1", "ds1", "build", "hash-x", 24);

        found.ShouldBeNull();
    }

    [Test]
    public async Task FindDedupCandidate_DifferentHash_NoMatch()
    {
        await using var ctx = NewContext(
            Job("a", "Processing", requestHash: "hash-x"));
        var query = new JobIndexQuery(ctx);

        var found = await query.FindDedupCandidateAsync("org1", "ds1", "build", "hash-y", 24);

        found.ShouldBeNull();
    }

    #endregion

    #region LogRingBuffer

    [Test]
    public void LogRingBuffer_Append_TracksCursorAndLines()
    {
        var buf = new LogRingBuffer(maxLines: 100, maxBytes: 100_000);
        buf.Append("line 1");
        buf.Append("line 2");

        var snap = buf.Snapshot();
        snap.Lines.Count.ShouldBe(2);
        snap.Cursor.ShouldBe(2);
        snap.TruncatedFromTail.ShouldBe(0);
    }

    [Test]
    public void LogRingBuffer_EvictsByMaxLines_AndTracksTruncation()
    {
        var buf = new LogRingBuffer(maxLines: 3, maxBytes: 100_000);
        for (var i = 0; i < 10; i++)
            buf.Append($"line {i}");

        var snap = buf.Snapshot();
        snap.Lines.Count.ShouldBe(3);
        snap.Cursor.ShouldBe(10);
        snap.TruncatedFromTail.ShouldBe(7);
        snap.Lines.Last().Msg.ShouldBe("line 9");
    }

    [Test]
    public void LogRingBuffer_RoundTripsThroughJson()
    {
        var buf = new LogRingBuffer(maxLines: 100, maxBytes: 100_000);
        buf.Append("hello", level: "WARN", phase: "p1");

        var json = buf.ToJson();
        json.ShouldContain("hello");
        json.ShouldContain("p1");
    }

    #endregion
}
