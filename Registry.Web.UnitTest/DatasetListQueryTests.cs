using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>The organization page searches, sorts and pages its datasets on the server.</summary>
[TestFixture]
public class DatasetListQueryTests
{
    private static DatasetDto Ds(string slug, string? name = null, long size = 0, long entries = 0, int visibility = 0,
        string? tagline = null, int day = 1) => new()
    {
        Slug = slug,
        Size = size,
        CreationDate = new DateTime(2026, 1, day),
        Properties = TestJson.Props(
            ("entries", entries),
            ("meta", new Dictionary<string, object?>
            {
                ["name"] = name == null ? null : new { data = name },
                ["tagline"] = tagline == null ? null : new { data = tagline },
                ["visibility"] = new { data = visibility }
            }))
    };

    private static readonly DatasetDto[] Datasets =
    [
        Ds("beta", "Zeta survey", size: 30, entries: 5, visibility: 2, day: 3),
        Ds("alpha", null, size: 10, entries: 50, visibility: 0, tagline: "coastal orthophoto", day: 1),
        Ds("gamma", "Mapping", size: 20, entries: 1, visibility: 1, day: 2)
    ];

    private static string[] Slugs(DatasetListQuery q) => q.Apply(Datasets).Select(d => d.Slug).ToArray();

    [Test]
    public void SortsByTheNameTheHubShows_ThenByTheOtherColumns()
    {
        Slugs(new DatasetListQuery()).ShouldBe(["alpha", "gamma", "beta"]); // alpha (slug), Mapping, Zeta survey
        Slugs(new DatasetListQuery(Sort: "name", Descending: true)).ShouldBe(["beta", "gamma", "alpha"]);
        Slugs(new DatasetListQuery(Sort: "size")).ShouldBe(["alpha", "gamma", "beta"]);
        Slugs(new DatasetListQuery(Sort: "entries", Descending: true)).ShouldBe(["alpha", "beta", "gamma"]);
        Slugs(new DatasetListQuery(Sort: "visibility")).ShouldBe(["alpha", "gamma", "beta"]);
        Slugs(new DatasetListQuery(Sort: "creationDate", Descending: true)).ShouldBe(["beta", "gamma", "alpha"]);
    }

    [Test]
    public void SearchesNameSlugAndTagline_AndPages()
    {
        Slugs(new DatasetListQuery(Query: "COASTAL")).ShouldBe(["alpha"]);
        Slugs(new DatasetListQuery(Query: "map")).ShouldBe(["gamma"]);
        Slugs(new DatasetListQuery(Query: "et")).ShouldBe(["beta"]); // slug "beta", name "Zeta"
        Slugs(new DatasetListQuery(Skip: 1, Take: 1)).ShouldBe(["gamma"]);
        Slugs(new DatasetListQuery(Skip: 5, Take: 10)).ShouldBeEmpty();
    }

    [Test]
    public void RestrictsToTheRequestedSlugs()
    {
        Slugs(new DatasetListQuery(Slugs: ["gamma", "beta", "missing"])).ShouldBe(["gamma", "beta"]);
    }
}
