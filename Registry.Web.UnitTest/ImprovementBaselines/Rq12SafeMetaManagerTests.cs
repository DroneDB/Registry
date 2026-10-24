#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-12 item 11: <see cref="SafeMetaManager"/> reads a dataset's well-known meta (name, visibility, ...) and
/// answers null for a value that is not there. It caught every <see cref="DdbException"/>, so a busy index, a
/// build holding it, a full disk or a shutting-down library read as "no name" or "no visibility", which the
/// access check turns into "private". Those are transient: they propagate, and the API answers 503 as for any
/// native call. A missing value is still null.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq12SafeMetaManagerTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public sealed class Rq12SafeMetaManagerTests
{
    private static readonly Dictionary<string, Func<SafeMetaManager, object?>> Getters = new()
    {
        ["Name"] = m => m.Name,
        ["Visibility"] = m => m.Visibility,
        ["IsPublic"] = m => m.IsPublic,
        ["Tagline"] = m => m.Tagline
    };

    private static IEnumerable<TestCaseData> Transient()
    {
        foreach (var getter in Getters.Keys)
        {
            yield return new TestCaseData(getter, new DdbBusyException("busy")).SetName($"{getter}_Busy_Propagates");
            yield return new TestCaseData(getter, new DdbBuildInProgressException("building"))
                .SetName($"{getter}_BuildInProgress_Propagates");
            yield return new TestCaseData(getter, new DdbRetryableException("disk full"))
                .SetName($"{getter}_Retryable_Propagates");
            yield return new TestCaseData(getter, new DdbShutdownException("shutting down"))
                .SetName($"{getter}_Shutdown_Propagates");
        }
    }

    private static IEnumerable<TestCaseData> Missing()
    {
        foreach (var getter in Getters.Keys)
        {
            yield return new TestCaseData(getter, new DdbException("Key not found")).SetName($"{getter}_Missing_IsNull");
            yield return new TestCaseData(getter, new DdbNotFoundException("Key not found"))
                .SetName($"{getter}_NotFound_IsNull");
        }
    }

    [TestCaseSource(nameof(Transient))]
    public void TransientError_Propagates(string getter, DdbException error)
    {
        var meta = Throwing(error);

        var thrown = Should.Throw<DdbException>(() => Getters[getter](meta));
        thrown.ShouldBeSameAs(error);
    }

    [TestCaseSource(nameof(Missing))]
    public void MissingValue_IsNull(string getter, DdbException error)
    {
        Getters[getter](Throwing(error)).ShouldBeNull();
    }

    /// <summary>
    /// The visibility cache reads the visibility through <see cref="SafeMetaManager"/>. A busy index read as
    /// "no visibility" was cached as private for the cache's expiry, so a public dataset answered 401/403 to
    /// everyone until then. Now the busy read fails that request only, and nothing is cached.
    /// </summary>
    [Test]
    public async Task VisibilityCache_BusyIndex_IsNotCachedAsPrivate()
    {
        var meta = new Mock<IMetaManager>();
        meta.SetupSequence(m => m.Get<int>(SafeMetaManager.VisibilityField, It.IsAny<string?>()))
            .Throws(new DdbBusyException("database is locked"))
            .Returns((int)Visibility.Public);
        var ddb = new Mock<IDDB>();
        ddb.SetupGet(d => d.Meta).Returns(meta.Object);
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get("org", It.IsAny<Guid>())).Returns(ddb.Object);

        var cache = new CacheManager(new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())),
            NullLogger<CacheManager>.Instance, new NullCacheKeyScanner(NullLogger<NullCacheKeyScanner>.Instance));
        cache.Register(MagicStrings.DatasetVisibilityCacheSeed, CacheProviderFactories.CreateDatasetVisibilityProvider(),
            TimeSpan.FromMinutes(5));
        var internalRef = Guid.NewGuid();

        await Should.ThrowAsync<DdbBusyException>(() =>
            cache.GetDatasetVisibilityAsync("org", internalRef, ddbManager.Object));
        (await cache.GetDatasetVisibilityAsync("org", internalRef, ddbManager.Object)).ShouldBe(Visibility.Public);
    }

    private static SafeMetaManager Throwing(DdbException error)
    {
        var manager = new Mock<IMetaManager>();
        manager.Setup(m => m.Get<string>(It.IsAny<string>(), It.IsAny<string?>())).Throws(error);
        manager.Setup(m => m.Get<int>(It.IsAny<string>(), It.IsAny<string?>())).Throws(error);
        manager.Setup(m => m.Get<bool>(It.IsAny<string>(), It.IsAny<string?>())).Throws(error);
        return new SafeMetaManager(manager.Object);
    }
}
