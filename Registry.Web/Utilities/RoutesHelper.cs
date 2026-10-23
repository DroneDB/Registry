using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Utilities;

public static class RoutesHelper
{
    public const string OrganizationsRadix = "orgs";
    public const string DatasetRadix = "ds";

    /// <summary>The route constraint of a slug segment (<see cref="SlugRouteConstraint"/>, registered with the API).</summary>
    public const string SlugConstraint = "slug";

    public const string OrganizationSlug = "{orgSlug:" + SlugConstraint + "}";
    public const string DatasetSlug = "{dsSlug:" + SlugConstraint + "}";
    public const string ShareRadix = "share";
    public const string UsersRadix = "users";
    public const string ObjectsRadix = "obj";
    public const string SystemRadix = "sys";
    public const string PushRadix = "push";
    public const string MetaRadix = "meta";
    public const string StacRadix = "stac";

}