#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// What a STAC landing page costs once the instance is warm (another caller, so the listing itself
/// is computed again). Before, every dataset's name was a distributed-cache round trip (and the datasets were
/// loaded with tracking, synchronously); now the names, like the visibility, are kept in process memory, so the
/// listing makes a constant number of distributed-cache calls whatever the number of datasets. Red before the
/// change (one read per dataset).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~StacListingCostTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public sealed class StacListingCostTests : IDisposable
{
    private const int Datasets = 20;
    private const string Org = "staclistingcost-cost-org";

    private readonly CountingCache _cache = new();
    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory(configureServices: services =>
            services.AddSingleton<IDistributedCache>(_cache));
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string ddbPath, string key, string? _) => key switch
            {
                "visibility" => $$"""{"data":{{(int)Visibility.Public}},"id":"v","mtime":0}""",
                "name" => $$"""{"data":"Name {{ddbPath.Length}}","id":"n","mtime":0}""",
                _ => throw new DdbException("Key not found")
            });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync("staclistingcost-cost-owner");
        await _factory.CreateUserAsync("staclistingcost-cost-1");
        await _factory.CreateUserAsync("staclistingcost-cost-2");
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: true);
        for (var i = 0; i < Datasets; i++)
            Seed.Dataset(context, org, $"ds-{i:D2}");
        await context.SaveChangesAsync();
    }

    [Test]
    public async Task WarmLandingPage_ForAnotherCaller_MakesNoCacheRoundTripPerDataset()
    {
        (await LandingPageChildren("staclistingcost-cost-1")).ShouldBe(Datasets);

        _cache.Reset();
        _factory.DdbWrapper.Invocations.Clear();
        (await LandingPageChildren("staclistingcost-cost-2")).ShouldBe(Datasets);

        TestContext.Out.WriteLine($"Distributed cache: {_cache.Reads} reads, {_cache.Writes} writes for {Datasets} datasets");
        _cache.Reads.ShouldBeLessThan(Datasets / 2, "a constant number of reads, not one per dataset");
        _factory.DdbWrapper.Invocations.Count(i => i.Method.Name == nameof(FakeIndexCalls.MetaGet)).ShouldBe(0,
            "the names and visibilities come from the caches");
    }

    private async Task<int> LandingPageChildren(string user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/stac").AsUser(user);
        var response = await _client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return body["links"]!.AsArray().Count(l => (string)l!["rel"]! == "child" &&
                                                   ((string)l["href"]!).Contains("/" + Org + "/", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    // The name of the native meta read, for the invocation filter
    private static class FakeIndexCalls
    {
        public const string MetaGet = nameof(MetaGet);
    }

    /// <summary>A memory distributed cache that counts its reads and writes.</summary>
    private sealed class CountingCache : IDistributedCache
    {
        private readonly MemoryDistributedCache _inner =
            new(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        private int _reads;
        private int _writes;

        public int Reads => _reads;
        public int Writes => _writes;

        public void Reset()
        {
            Interlocked.Exchange(ref _reads, 0);
            Interlocked.Exchange(ref _writes, 0);
        }

        public byte[]? Get(string key)
        {
            Interlocked.Increment(ref _reads);
            return _inner.Get(key);
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            Interlocked.Increment(ref _reads);
            return _inner.GetAsync(key, token);
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            Interlocked.Increment(ref _writes);
            _inner.Set(key, value, options);
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            Interlocked.Increment(ref _writes);
            return _inner.SetAsync(key, value, options, token);
        }

        public void Refresh(string key) => _inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);
        public void Remove(string key) => _inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);
    }
}
