#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Tests for <see cref="ExtractionBudget"/>, the shared decompression-bomb / disk-space guard. The
/// deterministic uncompressed-size cap is exercised directly; the disk-space net is verified only in
/// its disabled form (margin = 0) because the free space of the test volume cannot be forced.
/// </summary>
[TestFixture]
public class ExtractionBudgetTests
{
    // Disk checks are disabled (margin 0) for the cap-focused tests so they are volume-independent.
    private static ExtractionBudget CapOnly(long maxBytes)
        => new(maxBytes, Path.GetTempPath(), safetyMarginBytes: 0);

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
    public void GetAvailableDiskBytes_ForTempPath_IsPositive()
    {
        ExtractionBudget.GetAvailableDiskBytes(Path.GetTempPath()).ShouldBeGreaterThan(0);
    }
}
