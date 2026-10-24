#nullable enable
using System;
using System.Linq;
using Registry.Web.Data.Models;

namespace Registry.Web.Models;

/// <summary>
/// The datasets a system operation (cleanup, rescan) targets: every dataset of every
/// organization, every dataset of one organization, or one dataset.
/// </summary>
public sealed record DatasetScope
{
    private DatasetScope(string? organizationSlug, string? datasetSlug)
    {
        OrganizationSlug = organizationSlug;
        DatasetSlug = datasetSlug;
    }

    /// <summary>The organization, or null for all organizations.</summary>
    public string? OrganizationSlug { get; }

    /// <summary>The dataset, or null for all datasets of the scope.</summary>
    public string? DatasetSlug { get; }

    public bool IsSingleDataset => DatasetSlug != null;

    /// <summary>Blank slugs mean "all".</summary>
    /// <exception cref="ArgumentException">A dataset slug without an organization slug.</exception>
    public static DatasetScope Parse(string? organizationSlug, string? datasetSlug)
    {
        var org = string.IsNullOrWhiteSpace(organizationSlug) ? null : organizationSlug.Trim();
        var ds = string.IsNullOrWhiteSpace(datasetSlug) ? null : datasetSlug.Trim();

        if (ds != null && org == null)
            throw new ArgumentException("DatasetSlug requires OrganizationSlug");

        return new DatasetScope(org, ds);
    }

    /// <summary>Restricts <paramref name="datasets"/> to the scope.</summary>
    public IQueryable<Dataset> Apply(IQueryable<Dataset> datasets)
    {
        if (OrganizationSlug != null)
            datasets = datasets.Where(d => d.Organization.Slug == OrganizationSlug);
        if (DatasetSlug != null)
            datasets = datasets.Where(d => d.Slug == DatasetSlug);
        return datasets;
    }

    /// <summary>
    /// Throws when a scope that names an organization or a dataset resolved to no dataset.
    /// An empty "all organizations" scope is valid.
    /// </summary>
    /// <exception cref="ArgumentException">The organization or the dataset does not exist.</exception>
    public void EnsureFound(int datasetCount)
    {
        if (datasetCount > 0 || OrganizationSlug == null)
            return;

        throw new ArgumentException(DatasetSlug != null
            ? $"Dataset '{OrganizationSlug}/{DatasetSlug}' not found"
            : $"Organization '{OrganizationSlug}' has no datasets or does not exist");
    }

    public override string ToString() =>
        OrganizationSlug == null ? "all organizations"
        : DatasetSlug == null ? OrganizationSlug
        : $"{OrganizationSlug}/{DatasetSlug}";
}
