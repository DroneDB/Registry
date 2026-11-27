using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Identity;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.OrganizationAssets;
using Registry.Web.Services.Organizations;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test.Support;

/// <summary>
/// An <see cref="OrganizationsManager"/> over its collaborators, for tests that build it by hand (the manager
/// takes the services it was split into; this wires them the way the container does).
/// </summary>
public static class TestOrganizations
{
    public static OrganizationsManager Create(IAuthManager authManager, IDatasetAccessResolver access,
        RegistryContext context, IDatasetResolver datasets, IDatasetsManager datasetsManager,
        ApplicationDbContext appContext, IOptions<AccountOptions> accounts, OrganizationMembershipService memberships,
        IDdbManager? ddbManager = null)
    {
        var assets = new OrganizationAssetStore(Mock.Of<IFileSystem>(),
            Microsoft.Extensions.Options.Options.Create(new StorageOptions()), NullLogger<OrganizationAssetStore>.Instance);
        var queries = new OrganizationQueries(authManager, access, context, appContext, ddbManager ?? Mock.Of<IDdbManager>(),
            new OrganizationLogoLinks(assets));
        return new OrganizationsManager(
            queries,
            new OrganizationCommands(authManager, access, context, datasets, datasetsManager, queries, assets),
            new OrganizationMerger(authManager, memberships, context, datasets, datasetsManager,
                assets, NullLogger<OrganizationMerger>.Instance),
            new OrganizationMembers(authManager, memberships, context, appContext, accounts,
                new OrganizationRights(authManager, context, datasets)));
    }
}
