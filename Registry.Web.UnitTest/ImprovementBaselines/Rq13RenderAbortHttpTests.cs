#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-13 through HTTP on <see cref="RegistryWebFactory"/>: a client that goes away stops the native render it
/// asked for. The thumbnail runs the production path (controller → render service → cache single-flight →
/// gated thumbnail generator → local generator → <see cref="IDdbWrapper"/>); the fake wrapper stands for libddb:
/// it reads the <see cref="DdbCancellation"/> the call envelope would hand to libddb as a cancel token, signals
/// that the render started, and runs until that token is canceled, then answers like a canceled libddb call.
/// Before, the request's token reached neither the cache's computation nor the gate's work, so the render ran
/// to its end for nobody. Latches only.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq13RenderAbortHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class Rq13RenderAbortHttpTests : IDisposable
{
    private const string OwnerUser = "rq13-render-owner";
    private const string Org = "rq13-render-org";
    private const string Ds = "rq13-render-ds";
    private const string Photo = "photo.jpg";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        // A dataset without visibility metadata is private (native ddb throws on a missing key)
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
        _factory.DdbWrapper.Setup(d => d.GetEntry(It.IsAny<string>(), Photo))
            .Returns(new Entry { Path = Photo, Type = EntryType.GeoImage, Hash = "rq13-photo", Size = 3 });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(OwnerUser);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: false);
        var ds = Seed.Dataset(context, org, Ds);
        await context.SaveChangesAsync();

        // The local generator reads the file before it renders it
        var ddb = scope.ServiceProvider.GetRequiredService<IDdbManager>().Get(Org, ds.InternalRef);
        await File.WriteAllBytesAsync(Path.Combine(ddb.DatasetFolderPath, Photo), [1, 2, 3]);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task AClientThatGoesAway_StopsTheRender_AndFreesTheGate()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var safety = new ManualResetEventSlim();
        _factory.DdbWrapper.Setup(d => d.GenerateThumbnail(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(() =>
            {
                // What the envelope would set on the native call: the render stops at its next checkpoint
                var token = DdbCancellation.Current;
                started.TrySetResult();
                WaitHandle.WaitAny([token.WaitHandle, safety.WaitHandle]);
                stopped.TrySetResult(token.IsCancellationRequested);
                throw new DdbCanceledException("Canceled by the cancel token (DDBCancel)", token);
            });
        var gate = _factory.Services.GetRequiredService<INativeWorkGate>();

        try
        {
            using var client = new CancellationTokenSource();
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"/orgs/{Org}/ds/{Ds}/thumb?path={Photo}&size=256").AsUser(OwnerUser);
            var response = _client.SendAsync(request, client.Token);

            await started.Task.WaitAsync(Guard);
            gate.InFlight(NativeLane.Heavy).ShouldBe(1);

            await client.CancelAsync();

            (await stopped.Task.WaitAsync(Guard)).ShouldBeTrue("the render saw its token canceled");
            await Should.ThrowAsync<OperationCanceledException>(() => response);
        }
        finally
        {
            // Never leaves a pool thread blocked, whatever failed above
            safety.Set();
        }
    }

    [Test]
    public async Task ACancellationTheClientDidNotAskFor_Is503WithABody()
    {
        // The host is stopping (or a job's token): libddb answers CANCELED while the client still waits
        _factory.DdbWrapper.Setup(d => d.GenerateThumbnail(It.IsAny<string>(), It.IsAny<int>()))
            .Throws(new DdbCanceledException("Canceled by the cancel token (DDBCancel)"));

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/orgs/{Org}/ds/{Ds}/thumb?path={Photo}&size=64").AsUser(OwnerUser);
        using var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        response.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(5));
        (await response.Content.ReadAsStringAsync()).ShouldContain(Utilities.ApiExceptionClassifier.CanceledMessage);
    }

    [Test]
    public async Task AClientThatStays_GetsItsThumbnail()
    {
        _factory.DdbWrapper.Setup(d => d.GenerateThumbnail(It.IsAny<string>(), It.IsAny<int>()))
            .Returns([0x52, 0x49, 0x46, 0x46]);

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/orgs/{Org}/ds/{Ds}/thumb?path={Photo}&size=128").AsUser(OwnerUser);
        using var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe([0x52, 0x49, 0x46, 0x46]);
    }
}
