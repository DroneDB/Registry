#nullable enable
using System;
using System.Linq;
using NUnit.Framework;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class DatasetScopeTest
{
    private static readonly Dataset[] Datasets =
    [
        Ds("org-a", "one"), Ds("org-a", "two"), Ds("org-b", "one")
    ];

    [Test]
    public void Parse_BlankSlugs_MeanAll()
    {
        var scope = DatasetScope.Parse(" ", null);

        scope.OrganizationSlug.ShouldBeNull();
        scope.DatasetSlug.ShouldBeNull();
        scope.IsSingleDataset.ShouldBeFalse();
        scope.ToString().ShouldBe("all organizations");
        Apply(scope).ShouldBe(["org-a/one", "org-a/two", "org-b/one"]);
    }

    [Test]
    public void Parse_TrimsSlugs()
    {
        var scope = DatasetScope.Parse(" org-a ", " two ");

        scope.ToString().ShouldBe("org-a/two");
        scope.IsSingleDataset.ShouldBeTrue();
        Apply(scope).ShouldBe(["org-a/two"]);
    }

    [Test]
    public void Apply_Organization_KeepsItsDatasets()
    {
        var scope = DatasetScope.Parse("org-a", null);

        scope.ToString().ShouldBe("org-a");
        Apply(scope).ShouldBe(["org-a/one", "org-a/two"]);
    }

    [Test]
    public void Parse_DatasetWithoutOrganization_Throws()
    {
        Should.Throw<ArgumentException>(() => DatasetScope.Parse(null, "one"));
    }

    [Test]
    public void EnsureFound_EmptyNamedScope_Throws()
    {
        Should.Throw<ArgumentException>(() => DatasetScope.Parse("missing", null).EnsureFound(0))
            .Message.ShouldContain("Organization 'missing'");
        Should.Throw<ArgumentException>(() => DatasetScope.Parse("org-a", "missing").EnsureFound(0))
            .Message.ShouldContain("Dataset 'org-a/missing' not found");
    }

    [Test]
    public void EnsureFound_EmptyAllScopeOrFoundDatasets_DoesNotThrow()
    {
        DatasetScope.Parse(null, null).EnsureFound(0);
        DatasetScope.Parse("org-a", null).EnsureFound(2);
    }

    private static string[] Apply(DatasetScope scope) =>
        scope.Apply(Datasets.AsQueryable()).Select(d => $"{d.Organization.Slug}/{d.Slug}").ToArray();

    private static Dataset Ds(string org, string slug) =>
        new() { Slug = slug, Organization = new Organization { Slug = org } };
}
