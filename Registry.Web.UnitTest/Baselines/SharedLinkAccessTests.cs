using System;
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
/// Shared links of a private organization, through HTTP (SEC-004): an anonymous visitor reads an Unlisted
/// and a Public dataset of a private organization, and nothing else. The Hub looks the organization up
/// without its global 401 handler (FR-XC-05) so the visitor stays on the dataset page; that look-up is
/// still refused, a private dataset of the organization is still refused, and so is any write to the
/// shared datasets.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~SharedLinkAccessTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class SharedLinkAccessTests : DatasetAccessFixture
{
    private const string UnlistedDs = "datasetaccess-unlisted-ds";
    private const string PublicDs = "datasetaccess-public-ds";

    private const string Org = "/orgs/" + PrivateOrg;
    private const string Unlisted = Org + "/ds/" + UnlistedDs;
    private const string Public = Org + "/ds/" + PublicDs;
    private const string Private = Org + "/ds/" + PrivateDs;

    protected override async Task SeedAsync(RegistryContext context)
    {
        var org = await context.Organizations.SingleAsync(o => o.Slug == PrivateOrg);
        org.IsPublic.ShouldBeFalse();

        var visibilities = new Dictionary<Guid, Visibility>
        {
            [Seed.Dataset(context, org, UnlistedDs).InternalRef] = Visibility.Unlisted,
            [Seed.Dataset(context, org, PublicDs).InternalRef] = Visibility.Public
        };
        await context.SaveChangesAsync();

        // The visibility of the shared datasets; any other dataset (and any other key) has no
        // metadata, so it stays private
        Factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), SafeMetaManager.VisibilityField, It.IsAny<string?>()))
            .Returns((string ddbPath, string _, string? _) =>
                Guid.TryParse(Path.GetFileName(ddbPath.TrimEnd('/', '\\')), out var internalRef) &&
                visibilities.TryGetValue(internalRef, out var visibility)
                    ? $$"""{"data":{{(int)visibility}},"id":"v","mtime":0}"""
                    : throw new DdbException("Key not found"));

        // The dataset entry (GET /orgs/{org}/ds/{ds}) of an empty index
        Factory.DdbWrapper.Setup(d => d.Info(It.IsAny<string>(), false, 0, false))
            .Returns((string path, bool _, int _, bool _) =>
            [
                new Entry
                {
                    Path = "file://" + path.Replace('\\', '/'),
                    Type = EntryType.DroneDB,
                    Properties = new Dictionary<string, JsonElement> { ["entries"] = JsonSerializer.SerializeToElement(0) }
                }
            ]);
    }

    [TestCase(Unlisted)]
    [TestCase(Public)]
    public async Task SharedDataset_Anonymous_Reads(string url)
    {
        var (response, body, _) = await SendAsync(Request(HttpMethod.Get, url, Caller.Anonymous));

        ((int)response.StatusCode).ShouldBe(200, body);
    }

    [TestCase("")]
    [TestCase("/")]
    public async Task PrivateOrganization_Anonymous_Is401(string suffix)
    {
        // The look-up the Hub's header makes on a dataset page, without the global 401 handler
        var (response, _, problem) = await SendAsync(Request(HttpMethod.Get, Org + suffix, Caller.Anonymous));

        ShouldBeProblem(response, problem, 401, "The current user is not allowed to read this organization");
    }

    [Test]
    public async Task PrivateDatasetOfTheOrganization_Anonymous_Is401()
    {
        var (response, _, problem) = await SendAsync(Request(HttpMethod.Get, Private, Caller.Anonymous));

        ShouldBeProblem(response, problem, 401, "The current user is not allowed to read this dataset");
    }

    [TestCase(Unlisted)]
    [TestCase(Public)]
    public async Task SharedDataset_Anonymous_CannotWrite(string url)
    {
        var (meta, _, metaProblem) = await SendAsync(Request(HttpMethod.Post, url + "/meta/set", Caller.Anonymous,
            Form(("key", "name"), ("data", "\"taken\""))));
        ShouldBeProblem(meta, metaProblem, 401, "The current user is not allowed to write to this dataset");

        var (clear, _, clearProblem) = await SendAsync(Request(HttpMethod.Post, url + "/tasks/clear", Caller.Anonymous));
        ShouldBeProblem(clear, clearProblem, 401, "The current user is not allowed to write to this dataset");

        var (delete, _, deleteProblem) = await SendAsync(Request(HttpMethod.Delete, url, Caller.Anonymous));
        ShouldBeProblem(delete, deleteProblem, 401, "The current user is not allowed to delete this dataset");

        Factory.DdbWrapper.Verify(d => d.MetaSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>()), Times.Never);
        var slug = url[(url.LastIndexOf('/') + 1)..];
        (await WithContextAsync(c => c.Datasets.AnyAsync(d => d.Slug == slug && d.Organization.Slug == PrivateOrg)))
            .ShouldBeTrue("the dataset is still there");
    }
}
