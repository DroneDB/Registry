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
        string? tagline = null, int day = 1, int? modified = null) => new()
    {
        Slug = slug,
        Size = size,
        CreationDate = new DateTime(2026, 1, day),
        LastModified = modified is { } hour ? new DateTime(2026, 2, 1, hour, 0, 0, DateTimeKind.Utc) : null,
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
        Ds("beta", "Zeta survey", size: 30, entries: 5, visibility: 2, day: 3, modified: 9),
        Ds("alpha", null, size: 10, entries: 50, visibility: 0, tagline: "coastal orthophoto", day: 1, modified: 12),
        Ds("gamma", "Mapping", size: 20, entries: 1, visibility: 1, day: 2, modified: 10)
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
    public void SortsByTheLastModifiedDate_WithTheSlugBreakingTies_AndUnknownDatesLastInEitherDirection()
    {
        // beta 9:00, gamma 10:00, alpha 12:00
        Slugs(new DatasetListQuery(Sort: "lastModified")).ShouldBe(["beta", "gamma", "alpha"]);
        Slugs(new DatasetListQuery(Sort: "LASTMODIFIED", Descending: true)).ShouldBe(["alpha", "gamma", "beta"]);

        DatasetDto[] withTies = [Ds("b", modified: 8), Ds("c", modified: 8), Ds("a", modified: 8), Ds("unknown"), Ds("d", modified: 7), Ds("also-unknown")];
        string[] Order(bool descending) => new DatasetListQuery(Sort: "lastModified", Descending: descending)
            .Apply(withTies).Select(d => d.Slug).ToArray();

        // Equal dates keep the slug order, in either direction; the datasets without a date come last both ways
        Order(false).ShouldBe(["d", "a", "b", "c", "also-unknown", "unknown"]);
        Order(true).ShouldBe(["a", "b", "c", "d", "also-unknown", "unknown"]);
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
