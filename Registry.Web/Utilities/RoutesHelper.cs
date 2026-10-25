namespace Registry.Web.Utilities;

/// <summary>
/// Route templates shared by the controllers. The names of the segments are part of the contract with the Hub, the
/// <c>ddb</c> CLI and scripts and are frozen as they are (<c>processingNodes</c>, <c>cleanupbatches</c>,
/// <c>check-artifact-completeness</c>, <c>thumb-ex</c>, ...): a rename would be an incompatible change, not tidying.
/// </summary>
public static class RoutesHelper
{
    public const string OrganizationsRadix = "orgs";
    public const string DatasetRadix = "ds";

    /// <summary>The route constraint of a slug segment (<see cref="SlugRouteConstraint"/>, registered with the API).</summary>
    public const string SlugConstraint = "slug";

    public const string OrganizationSlug = "{orgSlug:" + SlugConstraint + "}";
    public const string DatasetSlug = "{dsSlug:" + SlugConstraint + "}";

    /// <summary>The route of an organization: <c>orgs/{orgSlug}</c>. Controllers of its resources append their segment.</summary>
    public const string OrganizationPrefix = OrganizationsRadix + "/" + OrganizationSlug;

    /// <summary>The route of an organization's datasets: <c>orgs/{orgSlug}/ds</c>.</summary>
    public const string DatasetsPrefix = OrganizationPrefix + "/" + DatasetRadix;

    /// <summary>The route of a dataset: <c>orgs/{orgSlug}/ds/{dsSlug}</c> (the one spelling of it in the controllers).</summary>
    public const string DatasetPrefix = DatasetsPrefix + "/" + DatasetSlug;
    public const string ShareRadix = "share";
    public const string UsersRadix = "users";
    public const string ObjectsRadix = "obj";
    public const string SystemRadix = "sys";
    public const string PushRadix = "push";
    public const string MetaRadix = "meta";
    public const string StacRadix = "stac";

}