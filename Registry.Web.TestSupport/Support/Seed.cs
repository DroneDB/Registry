using System;
using System.Collections.Generic;
using Registry.Web.Data;
using Registry.Web.Data.Models;

namespace Registry.Web.Test.Support;

/// <summary>
/// Valid registry entities for test databases, replacing inline <c>new Organization { … }</c> /
/// <c>new Dataset { … }</c> literals. The helpers add the entities to the context and do not save,
/// so they compose inside a <see cref="TestDb.CreateContext{TContext}"/> seed callback that ends with
/// <c>SaveChanges()</c>.
/// </summary>
public static class Seed
{
    /// <summary>Internal reference of the default dataset in the test data archives (<c>Data/Ddb/public/…</c>).</summary>
    public static readonly Guid DefaultDatasetRef = Guid.Parse("0a223495-84a0-4c15-b425-c7ef88110e75");

    /// <summary>Adds an organization. <paramref name="name"/> defaults to the slug.</summary>
    public static Organization Organization(RegistryContext context, string slug, string? name = null,
        bool isPublic = false, string? ownerId = null, string? description = null)
    {
        var organization = new Organization
        {
            Slug = slug,
            Name = name ?? slug,
            Description = description,
            CreationDate = DateTime.UtcNow,
            IsPublic = isPublic,
            OwnerId = ownerId,
            Datasets = new List<Dataset>()
        };
        context.Organizations.Add(organization);
        return organization;
    }

    /// <summary>Adds a dataset to <paramref name="organization"/>. <paramref name="internalRef"/> defaults to a new GUID.</summary>
    public static Dataset Dataset(RegistryContext context, Organization organization, string slug, Guid? internalRef = null)
    {
        var dataset = new Dataset
        {
            Slug = slug,
            InternalRef = internalRef ?? Guid.NewGuid(),
            CreationDate = DateTime.UtcNow,
            Organization = organization
        };
        organization.Datasets ??= new List<Dataset>();
        organization.Datasets.Add(dataset);
        context.Datasets.Add(dataset);
        return dataset;
    }

    /// <summary>Adds the public organization (as the registry creates it).</summary>
    public static Organization PublicOrganization(RegistryContext context) =>
        Organization(context, MagicStrings.PublicOrganizationSlug, "Public", isPublic: true,
            description: "Public organization");

    /// <summary>
    /// Adds the public organization with its default dataset, whose internal reference is
    /// <see cref="DefaultDatasetRef"/> (the dataset the test archives contain).
    /// </summary>
    public static Dataset PublicDefaultDataset(RegistryContext context) =>
        Dataset(context, PublicOrganization(context), MagicStrings.DefaultDatasetSlug, DefaultDatasetRef);
}
