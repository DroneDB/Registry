using System.Threading.Tasks;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Services.Managers;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Test.Support;

/// <summary>Test shorthands over the access controls: the user as an object instead of its id.</summary>
public static class AccessControlTestExtensions
{
    public static Task<bool> CanAccessOrganization(this OrganizationAccessControl control, Organization org,
        AccessType access, User? user) => control.CanAccessOrganization(org, access, user?.Id!);

    public static Task<bool> CanAccessDataset(this DatasetAccessControl control, Dataset dataset, AccessType access,
        User? user) => control.CanAccessDataset(dataset, access, user?.Id!);
}
