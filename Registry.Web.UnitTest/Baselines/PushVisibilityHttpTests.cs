using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Services.Adapters;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// A visibility pushed with <c>ddb meta set visibility</c> and <c>ddb push</c> takes effect for the next request
/// (FR-DEV-03, SEC-002, AC-019): the whole push through HTTP (init, meta, commit) on a dataset whose visibility an
/// anonymous visitor has just read, so the server holds it in its cache (one day). A public dataset made private
/// answers that visitor 401 at once, a private one made public 200. The native calls of the push are set up on the
/// fake; the commit's apply writes the pushed visibility, as libddb restores the pushed meta.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~PushVisibilityHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class PushVisibilityHttpTests : DatasetAccessFixture
{
    private const string MadePrivateDs = "push-made-private-ds";
    private const string MadePublicDs = "push-made-public-ds";
    private const string Checksum = "push-visibility-checksum";

    // The visibility of each dataset in its index, by the dataset folder's name (its internal reference)
    private readonly ConcurrentDictionary<Guid, Visibility> _visibilities = new();

    private static Guid RefOf(string ddbPath) => Guid.Parse(Path.GetFileName(ddbPath.TrimEnd('/', '\\')));

    protected override async Task SeedAsync(RegistryContext context)
    {
        var org = await context.Organizations.SingleAsync(o => o.Slug == PrivateOrg);
        _visibilities[Seed.Dataset(context, org, MadePrivateDs).InternalRef] = Visibility.Public;
        _visibilities[Seed.Dataset(context, org, MadePublicDs).InternalRef] = Visibility.Private;
        await context.SaveChangesAsync();

        Factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), SafeMetaManager.VisibilityField, It.IsAny<string?>()))
            .Returns((string ddbPath, string _, string? _) =>
                _visibilities.TryGetValue(RefOf(ddbPath), out var visibility)
                    ? $$"""{"data":{{(int)visibility}},"id":"v","mtime":0}"""
                    : throw new DdbException("Key not found"));

        // The root of an empty index (GET .../list)
        Factory.DdbWrapper.Setup(d => d.ListEntries(It.IsAny<string>(), It.IsAny<EntryQuery>())).Returns(() => []);

        // The push: the client's stamp differs from ours by one meta, the pushed visibility
        Factory.DdbWrapper.Setup(d => d.GetStamp(It.IsAny<string>()))
            .Returns(() => new Stamp { Checksum = Checksum, Entries = [], Meta = [] });
        Factory.DdbWrapper.Setup(d => d.GetStampChecksum(It.IsAny<string>())).Returns(Checksum);
        Factory.DdbWrapper.Setup(d => d.Delta(It.IsAny<Stamp>(), It.IsAny<Stamp>()))
            .Returns(() => new Delta { MetaAdds = ["pushed-visibility"] });
        Factory.DdbWrapper.Setup(d => d.ComputeDeltaLocals(It.IsAny<Delta>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new Dictionary<string, bool>());
        // libddb restores the pushed meta the delta adds: here, the visibility
        Factory.DdbWrapper.Setup(d => d.ApplyDelta(It.IsAny<Delta>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<MergeStrategy>(), It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Returns((Delta _, string _, string ddbPath, MergeStrategy _, string? dump,
                IReadOnlyDictionary<string, string> _) =>
            {
                var pushed = JsonDocument.Parse(dump!).RootElement[0];
                pushed.GetProperty("key").GetString().ShouldBe(SafeMetaManager.VisibilityField);
                _visibilities[RefOf(ddbPath)] = (Visibility)pushed.GetProperty("data").GetInt32();
                return [];
            });
    }

    private static string List(string ds) => "/orgs/" + PrivateOrg + "/ds/" + ds + "/list";

    private async Task<int> AnonymousListAsync(string ds)
    {
        var (response, _, _) = await SendAsync(Request(HttpMethod.Get, List(ds), Caller.Anonymous));
        return (int)response.StatusCode;
    }

    // ddb push of a meta delta: init, the meta dump of what the delta adds, commit
    private async Task PushVisibilityAsync(string ds, Visibility visibility)
    {
        var push = "/orgs/" + PrivateOrg + "/ds/" + ds + "/push/";

        var (init, initBody, initJson) = await SendAsync(Request(HttpMethod.Post, push + "init", Caller.Owner,
            Form(("checksum", Checksum), ("stamp", """{"checksum":"client","entries":[],"meta":["pushed-visibility"]}"""))));
        ((int)init.StatusCode).ShouldBe(200, initBody);
        initJson.ShouldNotBeNull()["pullRequired"]!.GetValue<bool>().ShouldBeFalse();
        var token = initJson["token"]!.GetValue<string>();

        var (meta, metaBody, _) = await SendAsync(Request(HttpMethod.Post, push + "meta", Caller.Owner,
            Form(("token", token),
                ("meta", $$"""[{"id":"pushed-visibility","path":"","key":"visibility","data":{{(int)visibility}},"mtime":1}]"""))));
        ((int)meta.StatusCode).ShouldBe(200, metaBody);

        var (commit, commitBody, _) =
            await SendAsync(Request(HttpMethod.Post, push + "commit", Caller.Owner, Form(("token", token))));
        ((int)commit.StatusCode).ShouldBe(200, commitBody);
    }

    [Test]
    public async Task PublicDatasetMadePrivateByAPush_IsRefusedToAnonymous_AtOnce()
    {
        (await AnonymousListAsync(MadePrivateDs)).ShouldBe(200, "public, and now in the visibility cache");

        await PushVisibilityAsync(MadePrivateDs, Visibility.Private);

        (await AnonymousListAsync(MadePrivateDs)).ShouldBe(401);
    }

    [Test]
    public async Task PrivateDatasetMadePublicByAPush_IsReadByAnonymous_AtOnce()
    {
        (await AnonymousListAsync(MadePublicDs)).ShouldBe(401, "private, and now in the visibility cache");

        await PushVisibilityAsync(MadePublicDs, Visibility.Public);

        (await AnonymousListAsync(MadePublicDs)).ShouldBe(200);
    }
}
