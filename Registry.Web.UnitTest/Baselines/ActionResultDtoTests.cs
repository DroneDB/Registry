using System;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Models.DTO;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The named records that replace the anonymous objects of eight actions answer the same JSON: same member
/// names (camel case), same value types. Each is written by the host's output formatter next to the anonymous
/// object it replaces.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class ActionResultDtoTests : IDisposable
{
    private RegistryWebFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _factory = new RegistryWebFactory();

    /// <summary>NUnit disposes the fixture after its last test.</summary>
    public void Dispose() => _factory.Dispose();

    private async Task<string> Json(object value) => await MvcJson.WriteAsync(_factory.Services, value);

    [Test]
    public async Task Deleted_IsTheAnonymousDeleted() =>
        (await Json(new DeletedDto(true))).ShouldBe(await Json(new { deleted = true }));

    [Test]
    public async Task TasksCleared_IsTheAnonymousCleared() =>
        (await Json(new TasksClearedDto(3))).ShouldBe(await Json(new { cleared = 3 }));

    [Test]
    public async Task TaskRequeued_IsTheAnonymousRequeued() =>
        (await Json(new TaskRequeuedDto(false))).ShouldBe(await Json(new { requeued = false }));

    [Test]
    public async Task JobAccepted_IsTheAnonymousJobId() =>
        (await Json(new JobAcceptedDto("42"))).ShouldBe(await Json(new { JobId = "42" }));

    [Test]
    public async Task Ok_IsTheAnonymousOk() =>
        (await Json(OkDto.Success)).ShouldBe(await Json(new { ok = true }));

    [Test]
    public async Task TheRecords_AreWrittenAsTheHubReadsThem()
    {
        (await Json(new DeletedDto(true))).ShouldBe("""{"deleted":true}""");
        (await Json(new TasksClearedDto(3))).ShouldBe("""{"cleared":3}""");
        (await Json(new TaskRequeuedDto(false))).ShouldBe("""{"requeued":false}""");
        (await Json(new JobAcceptedDto("42"))).ShouldBe("""{"jobId":"42"}""");
        (await Json(OkDto.Success)).ShouldBe("""{"ok":true}""");
    }
}
