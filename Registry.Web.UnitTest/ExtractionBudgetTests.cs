#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Tests for <see cref="ExtractionBudget"/>, the shared decompression-bomb / disk-space guard, and
/// <see cref="VolumeHeadroom"/> (RQ-10), which creates it: the volume is measured through a function,
/// so the disk-space net is tested on a volume with a given free space, and its margin includes
/// libddb's free-disk floor (XI-06).
/// </summary>
[TestFixture]
public class ExtractionBudgetTests
{
    // Disk checks are disabled (margin 0) for the cap-focused tests so they are volume-independent.
    private static ExtractionBudget CapOnly(long maxBytes)
        => new(maxBytes, Path.GetTempPath(), safetyMarginBytes: 0, _ => long.MaxValue);

    private const long MiB = 1024L * 1024;

    [Test]
    public void Account_WithinCap_DoesNotThrowAndAccumulates()
    {
        var budget = CapOnly(1000);

        Should.NotThrow(() => budget.Account(600));
        budget.BytesWritten.ShouldBe(600);

        Should.NotThrow(() => budget.Account(400));
        budget.BytesWritten.ShouldBe(1000);
    }

    [Test]
    public void Account_ExceedingCap_ThrowsQuotaExceededAndDoesNotCountBreach()
    {
        var budget = CapOnly(1000);
        budget.Account(900);

        Should.Throw<QuotaExceededException>(() => budget.Account(200));
        budget.BytesWritten.ShouldBe(900);
    }

    [Test]
    public void Account_ZeroCap_IsUnlimited()
    {
        var budget = CapOnly(0);
        Should.NotThrow(() => budget.Account(long.MaxValue / 2));
    }

    [Test]
    public async Task CopyGuardedAsync_WithinCap_CopiesAllBytesAndReturnsCount()
    {
        var budget = CapOnly(0);
        var data = new byte[(3 * 1024 * 1024) + 123]; // spans several copy buffers
        new Random(42).NextBytes(data);

        using var source = new MemoryStream(data);
        using var dest = new MemoryStream();

        var written = await budget.CopyGuardedAsync(source, dest, CancellationToken.None);

        written.ShouldBe(data.Length);
        budget.BytesWritten.ShouldBe(data.Length);
        dest.ToArray().ShouldBe(data);
    }

    [Test]
    public async Task CopyGuardedAsync_ExceedingCap_ThrowsAndStopsAtCap()
    {
        var budget = CapOnly(1024 * 1024); // 1 MiB cap, 3 MiB source
        var data = new byte[3 * 1024 * 1024];

        using var source = new MemoryStream(data);
        using var dest = new MemoryStream();

        await Should.ThrowAsync<QuotaExceededException>(
            async () => await budget.CopyGuardedAsync(source, dest, CancellationToken.None));

        dest.Length.ShouldBeLessThanOrEqualTo(1024 * 1024);
        budget.BytesWritten.ShouldBeLessThanOrEqualTo(1024 * 1024);
    }

    [Test]
    public void Headroom_AvailableBytes_ForTempPath_IsPositive()
    {
        TestHeadroom.Default.AvailableBytes(Path.GetTempPath()).ShouldBeGreaterThan(0);
    }

    [Test]
    public void Headroom_FloorComesFromLibddbsEffectiveConfig()
    {
        // FakeDdbRuntime answers with the effective-config fixture: minFreeDiskMb 256
        TestHeadroom.Default.FloorBytes.ShouldBe(256 * MiB);
        TestHeadroom.WithFree(0, floorMb: 512).FloorBytes.ShouldBe(512 * MiB);
    }

    [Test]
    public void Headroom_MarginIsTheSafetyMarginPlusTheFloor_AndZeroTurnsTheGuardOff()
    {
        var headroom = TestHeadroom.WithFree(0, floorMb: 512);

        headroom.MarginBytes(256 * MiB).ShouldBe(768 * MiB);
        headroom.MarginBytes(0).ShouldBe(0);
    }

    // XI-06: an extraction that would leave less than libddb needs stops; with the old margin alone
    // (256 MiB) this one would have gone on and left every build of the files without room
    [Test]
    public void Budget_StopsBeforeEatingIntoLibddbsFloor()
    {
        var budget = TestHeadroom.WithFree(1024 * MiB, floorMb: 512)
            .CreateBudget(maxBytes: 0, Path.GetTempPath(), safetyMarginBytes: 256 * MiB);

        Should.NotThrow(() => budget.Account(200 * MiB)); // 824 MiB left >= 768
        Should.Throw<QuotaExceededException>(() => budget.Account(100 * MiB)); // 724 MiB left < 768
        budget.BytesWritten.ShouldBe(200 * MiB);
    }

    [Test]
    public void Headroom_EnsureFree_KeepsTheMarginAndTheFloor()
    {
        Should.Throw<QuotaExceededException>(() =>
            TestHeadroom.WithFree(1024 * MiB, floorMb: 512).EnsureFree("/x", 300 * MiB, 256 * MiB));
        Should.NotThrow(() =>
            TestHeadroom.WithFree(1024 * MiB, floorMb: 0).EnsureFree("/x", 300 * MiB, 256 * MiB));
        Should.NotThrow(() =>
            TestHeadroom.WithFree(1024 * MiB, floorMb: 512).EnsureFree("/x", 300 * MiB, 0)); // guard off
    }

    [Test]
    public void Headroom_UnknownVolume_ReliesOnTheCapOnly()
    {
        var headroom = new VolumeHeadroom(new Registry.Adapters.Testing.FakeDdbRuntime(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VolumeHeadroom>.Instance, _ => null);

        headroom.AvailableBytes("/nowhere").ShouldBe(long.MaxValue);
        Should.NotThrow(() => headroom.EnsureFree("/nowhere", long.MaxValue / 2, 256 * MiB));
    }
}
