#nullable enable
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Registry.Web.Data;
using Registry.Web.Identity;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Organizations;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test.Support;

/// <summary>
/// An <see cref="OrganizationsManager"/> over its collaborators, for tests that build it by hand (RQ-12: the manager
/// takes the services it was split into; this wires them the way the container does).
/// </summary>
public static class TestOrganizations
{
    public static OrganizationsManager Create(IAuthManager authManager, IDatasetAccessResolver access,
        RegistryContext context, IDatasetResolver datasets, IDatasetsManager datasetsManager,
        ApplicationDbContext appContext, IOptions<AccountOptions> accounts, IOrganizationMembershipService memberships)
    {
        var queries = new OrganizationQueries(authManager, access, context, appContext);
        return new OrganizationsManager(
            queries,
            new OrganizationCommands(authManager, access, context, datasets, datasetsManager, queries),
            new OrganizationMerger(authManager, memberships, context, datasets, datasetsManager,
                NullLogger<OrganizationMerger>.Instance),
            new OrganizationMembers(authManager, memberships, context, appContext, accounts));
    }
}
