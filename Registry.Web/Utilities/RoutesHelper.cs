#nullable disable
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

    /// <summary>The literal between the hash of a build and the version of an artifact in a build file URL.</summary>
    public const string BuildVersionSeparator = "@";

    /// <summary>A file of a build, relative to <see cref="DatasetPrefix"/>: <c>build/{hash}/{path}</c>.</summary>
    public const string BuildFile = "build/{hash}/{*path}";

    /// <summary>
    /// A file of a build at a version of its artifact (libddb's <c>version</c>): <c>build/{hash}@{version}/{path}</c>.
    /// The version is a path segment, not a query parameter, so the relative references of a directory artifact (the
    /// tiles a 3D Tiles index names) resolve to URLs that carry it too.
    /// </summary>
    public const string VersionedBuildFile = "build/{hash}" + BuildVersionSeparator + "{version}/{*path}";

}