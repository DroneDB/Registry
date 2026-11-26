using System.Globalization;
using System.IO;
using Registry.Web.Services.OrganizationAssets;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Organizations;

/// <summary>
/// Where the logo of an organization is: the one place the answers that show an organization (its DTO in the lists, in
/// one organization and in the memberships of a user) get their <c>logoUrl</c> from. It needs only the store: the file
/// is the whole truth about whether there is a logo.
/// </summary>
public sealed class OrganizationLogoLinks
{
    private readonly OrganizationAssetStore _assets;

    public OrganizationLogoLinks(OrganizationAssetStore assets)
    {
        _assets = assets;
    }

    /// <summary>
    /// The URL of the logo of the organization, <c>/orgs/{slug}/logo?v={version}</c>, or null when it has none. The
    /// version is part of it so that a browser keeps the image for as long as it is the same one (and asks again as soon
    /// as it is replaced).
    /// </summary>
    public string? UrlOf(string orgSlug) => Versioned(orgSlug, $"/{RoutesHelper.OrganizationsRadix}/{orgSlug}/logo");

    /// <summary>
    /// The URL of the logo of the organization for a reader of one of its datasets,
    /// <c>/orgs/{slug}/ds/{dsSlug}/organization-logo?v={version}</c> (who may not read the organization itself), or null
    /// when it has none.
    /// </summary>
    public string? DatasetUrlOf(string orgSlug, string dsSlug) => Versioned(orgSlug,
        $"/{RoutesHelper.OrganizationsRadix}/{orgSlug}/{RoutesHelper.DatasetRadix}/{dsSlug}/organization-logo");

    private string? Versioned(string orgSlug, string path)
    {
        // An organization stored with a slug the rules refuse (not repaired yet) has no folder of the store
        if (!SlugRules.IsValid(orgSlug))
            return null;

        return _assets.InfoOf(orgSlug, OrganizationAssetStore.LogoFileName) is { } info
            ? $"{path}?v={VersionOf(info)}"
            : null;
    }

    /// <summary>The version of a logo file: the UTC ticks of its last write, which is also what its entity tag is made of.</summary>
    public static string VersionOf(FileInfo info) => info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
}
