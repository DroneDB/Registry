using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
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
        var registry = new HeavyToolRegistry([
            new FakeTool("build", "1"),
            new FakeTool("build", "2")
        ]);

        registry.Resolve("build", "1")!.Version.ShouldBe("1");
        registry.Resolve("build", "2")!.Version.ShouldBe("2");
    }

    [Test]
    public void Registry_Resolve_WithoutVersion_PicksHighest()
    {
        var registry = new HeavyToolRegistry([
            new FakeTool("build", "1"),
            new FakeTool("build", "2"),
            new FakeTool("build", "10")
        ]);

        registry.Resolve("build")!.Version.ShouldBe("10");
    }

    [Test]
    public void Registry_Resolve_UnknownTool_ReturnsNull()
    {
        var registry = new HeavyToolRegistry([new FakeTool("build", "1")]);

        registry.Resolve("does-not-exist").ShouldBeNull();
        registry.Resolve("build", "99").ShouldBeNull();
    }

    [Test]
    public void Registry_Resolve_IsCaseInsensitive()
    {
        var registry = new HeavyToolRegistry([new FakeTool("raster-export", "1")]);

        registry.Resolve("RASTER-EXPORT")!.Id.ShouldBe("raster-export");
    }

    [Test]
    public void Registry_DuplicateRegistration_Throws()
    {
        Should.Throw<InvalidOperationException>(() => new HeavyToolRegistry([
            new FakeTool("build", "1"),
            new FakeTool("build", "1")
        ]));
    }

    [Test]
    public void Registry_All_ExposesEveryTool()
    {
        var registry = new HeavyToolRegistry([
            new FakeTool("build", "1"),
            new FakeTool("raster-export", "1")
        ]);

        registry.All.Count.ShouldBe(2);
    }

    #endregion

    #region HeavyToolGating

    private static HeavyToolGating Gating(
        Dictionary<string, HeavyToolConfig> tools,
        bool isAdmin = false,
        IEnumerable<string>? roles = null)
    {
        var settings = new AppSettings
        {
            ProcessingPlatform = new ProcessingPlatformSettings { Tools = tools }
        };

        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.IsUserAdmin()).ReturnsAsync(isAdmin);
        var roleSet = new HashSet<string>(roles ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        auth.Setup(a => a.IsUserInRole(It.IsAny<string>()))
            .ReturnsAsync((string r) => roleSet.Contains(r));

        return new HeavyToolGating(Microsoft.Extensions.Options.Options.Create(settings), auth.Object);
    }

    [Test]
    public async Task Gating_UnlistedTool_IsEnabled()
    {
        var gating = Gating(new Dictionary<string, HeavyToolConfig>());

        var state = await gating.EvaluateAsync("build", orgSlug: "org1");

        state.Allowed.ShouldBeTrue();
        state.Hidden.ShouldBeFalse();
        state.Disabled.ShouldBeFalse();
    }

    [Test]
    public async Task Gating_AvailabilityHidden_IsHidden()
    {
        var gating = Gating(new Dictionary<string, HeavyToolConfig>
        {
            ["build"] = new() { Availability = HeavyToolAvailability.Hidden }
        });

        var state = await gating.EvaluateAsync("build", orgSlug: "org1");

        state.Hidden.ShouldBeTrue();
        state.Allowed.ShouldBeFalse();
    }

    [Test]
    public async Task Gating_AvailabilityDisabled_IsDisabledWithMessage()
    {
        var gating = Gating(new Dictionary<string, HeavyToolConfig>
        {
            ["build"] = new()
            {
                Availability = HeavyToolAvailability.Disabled,
                DisabledMessage = "Temporarily off"
            }
        });

        var state = await gating.EvaluateAsync("build", orgSlug: "org1");

        state.Disabled.ShouldBeTrue();
        state.Hidden.ShouldBeFalse();
        state.DisabledMessage.ShouldBe("Temporarily off");
    }

    [Test]
    public async Task Gating_RoleAllowlist_AdminAllowed()
    {
        var gating = Gating(new Dictionary<string, HeavyToolConfig>
        {
            ["photogrammetry"] = new() { AllowedRoles = { "admin" } }
        }, isAdmin: true);

        (await gating.EvaluateAsync("photogrammetry", "org1")).Allowed.ShouldBeTrue();
    }

    [Test]
    public async Task Gating_RoleAllowlist_NonAdminHiddenByDefault()
    {
        var gating = Gating(new Dictionary<string, HeavyToolConfig>
        {
            ["photogrammetry"] = new() { AllowedRoles = { "admin" } }
        }, isAdmin: false);

        (await gating.EvaluateAsync("photogrammetry", "org1")).Hidden.ShouldBeTrue();
    }

    [Test]
    public async Task Gating_RoleAllowlist_NonAdmin_DisabledWhenHideWhenNotAllowedFalse()
    {
        var gating = Gating(new Dictionary<string, HeavyToolConfig>
        {
            ["photogrammetry"] = new()
            {
                AllowedRoles = { "admin" },
                HideWhenNotAllowed = false,
                DisabledMessage = "Admins only"
            }
        }, isAdmin: false);

        var state = await gating.EvaluateAsync("photogrammetry", "org1");

        state.Disabled.ShouldBeTrue();
        state.DisabledMessage.ShouldBe("Admins only");
    }

    [Test]
    public async Task Gating_RoleAllowlist_CustomRoleMatches()
    {
        var gating = Gating(new Dictionary<string, HeavyToolConfig>
        {
            ["build"] = new() { AllowedRoles = { "power-user" } }
        }, isAdmin: false, roles: new[] { "power-user" });

        (await gating.EvaluateAsync("build", "org1")).Allowed.ShouldBeTrue();
    }

    [Test]
    public async Task Gating_OrgAllowlist_MatchingOrgAllowed()
    {
        var gating = Gating(new Dictionary<string, HeavyToolConfig>
        {
            ["archive-extract"] = new() { AllowedOrgs = { "acme" } }
        });

        (await gating.EvaluateAsync("archive-extract", "acme")).Allowed.ShouldBeTrue();
    }

    [Test]
    public async Task Gating_OrgAllowlist_OtherOrgHidden()
    {
        var gating = Gating(new Dictionary<string, HeavyToolConfig>
        {
            ["archive-extract"] = new() { AllowedOrgs = { "acme" } }
        });

        (await gating.EvaluateAsync("archive-extract", "other")).Hidden.ShouldBeTrue();
    }

    [Test]
    public async Task Gating_OrgAllowlist_SkippedWhenNoOrgContext()
    {
        // The features endpoint passes orgSlug = null; the org allowlist must be skipped.
        var gating = Gating(new Dictionary<string, HeavyToolConfig>
        {
            ["archive-extract"] = new() { AllowedOrgs = { "acme" } }
        });

        (await gating.EvaluateAsync("archive-extract", orgSlug: null)).Allowed.ShouldBeTrue();
    }

    [Test]
    public async Task Gating_KeyLookup_IsCaseInsensitive()
    {
        var gating = Gating(new Dictionary<string, HeavyToolConfig>
        {
            ["Build"] = new() { Availability = HeavyToolAvailability.Hidden }
        });

        (await gating.EvaluateAsync("build", "org1")).Hidden.ShouldBeTrue();
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

    [Test]
    public void LogTail_Parse_NullLinesFallback_EmptySnapshot()
    {
        var snap = LogTailSnapshot.Parse("{\"lines\":null,\"cursor\":5}");
        snap.Lines.ShouldBeEmpty();
        snap.AsStrings().ShouldBeEmpty();
    }

    [Test]
    public void LogTail_Parse_OutOfRangeOrNullOrEmptyEntries_Dropped()
    {
        var json = $$"""
            {"lines":[
               {"t":9223372036854775807,"lvl":"info","msg":"future"},
               null,
               {"t":{{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}},"lvl":"info","msg":"ok"}
            ],"cursor":3}
            """;
        var snap = LogTailSnapshot.Parse(json);

        snap.Lines.Count.ShouldBe(1);
        snap.Lines[0].Msg.ShouldBe("ok");
        snap.AsStrings().Single().ShouldContain("ok");
    }

    #endregion
}
