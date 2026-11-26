using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Exceptions;
using Registry.Web.Services.OrganizationAssets;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Services.Organizations;

/// <summary>The logo of an organization ready to be sent: where it is, the media type it is served as, and its state on the disk.</summary>
public sealed record OrganizationLogoFile(string Path, string MediaType, FileInfo Info)
{
    /// <summary>What the URL of the logo names (<see cref="OrganizationLogoLinks.UrlOf"/>): it changes when the logo is replaced.</summary>
    public string Version => OrganizationLogoLinks.VersionOf(Info);
}

/// <summary>
/// The logo of an organization. It is the file <see cref="OrganizationAssetStore.LogoFileName"/> of the organization's
/// folder and nothing else: no row says that there is one, so the disk is the only truth and a logo can never be named
/// without its file. Whoever may read the organization may read the logo; its administrators set and remove it.
/// </summary>
public sealed class OrganizationLogos
{
    /// <summary>The largest logo accepted, in bytes.</summary>
    public const int MaxBytes = 256 * 1024;

    private readonly OrganizationAssetStore _assets;
    private readonly OrganizationLogoLinks _links;
    private readonly IAuthManager _authManager;
    private readonly IDatasetAccessResolver _access;
    private readonly IDatasetResolver _datasets;
    private readonly OrganizationRights _rights;

    public OrganizationLogos(OrganizationAssetStore assets, OrganizationLogoLinks links, IAuthManager authManager, IDatasetAccessResolver access,
        IDatasetResolver datasets, OrganizationRights rights)
    {
        _assets = assets;
        _links = links;
        _authManager = authManager;
        _access = access;
        _datasets = datasets;
        _rights = rights;
    }

    /// <summary>
    /// Sets the logo (administrators of the organization): the bytes are checked (<see cref="OrganizationImage.Inspect"/>)
    /// and replace the one before, whatever its type was. Two requests at once leave the last one's.
    /// </summary>
    /// <returns>The URL of the new logo (<see cref="OrganizationLogoLinks.UrlOf"/>).</returns>
    /// <exception cref="ForbiddenException">The current user does not administer the organization.</exception>
    /// <exception cref="NotFoundException">There is no such organization.</exception>
    /// <exception cref="BadRequestException">Empty, over <see cref="MaxBytes"/>, or not a PNG, JPEG, WebP or SVG.</exception>
    public async Task<string?> SetAsync(string orgSlug, byte[] content, CancellationToken ct = default)
    {
        await RequireAdministratorAsync(orgSlug, ct);

        var image = OrganizationImage.Inspect(content, MaxBytes);
        _assets.Save(orgSlug, OrganizationAssetStore.LogoFileName, image.Content, replace: true);

        return _links.UrlOf(orgSlug);
    }

    /// <summary>Removes the logo (administrators of the organization); an organization without one is left as it is.</summary>
    /// <exception cref="ForbiddenException">The current user does not administer the organization.</exception>
    /// <exception cref="NotFoundException">There is no such organization.</exception>
    public async Task RemoveAsync(string orgSlug, CancellationToken ct = default)
    {
        await RequireAdministratorAsync(orgSlug, ct);

        _assets.Remove(orgSlug, OrganizationAssetStore.LogoFileName);
    }

    /// <summary>The logo, for whoever may read the organization (anyone, when it is public).</summary>
    /// <exception cref="NotFoundException">There is no such organization, or it has no logo.</exception>
    /// <exception cref="ForbiddenException">The current user may not read the organization.</exception>
    public async Task<OrganizationLogoFile> OpenAsync(string orgSlug, CancellationToken ct = default)
    {
        await _access.ResolveOrganizationAsync(orgSlug, AccessType.Read, ct: ct);
        return Find(orgSlug);
    }

    /// <summary>
    /// The logo, for a reader of one of the datasets of the organization: a public dataset of a private organization
    /// shows the logo of its organization (on a report, for one) to readers that may not read the organization.
    /// </summary>
    /// <exception cref="NotFoundException">There is no such dataset, or the organization has no logo.</exception>
    /// <exception cref="ForbiddenException">The current user may not read the dataset.</exception>
    public async Task<OrganizationLogoFile> OpenForDatasetAsync(string orgSlug, string dsSlug, CancellationToken ct = default)
    {
        await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);
        return Find(orgSlug);
    }

    private OrganizationLogoFile Find(string orgSlug)
    {
        const string noLogo = "The organization has no logo";
        var info = _assets.InfoOf(orgSlug, OrganizationAssetStore.LogoFileName)
                   ?? throw new NotFoundException(noLogo);

        // The file has no extension: its type is what its first bytes say (the upload let in only the types it knows,
        // and sanitised the SVG that is none of the others)
        var head = _assets.ReadHead(orgSlug, OrganizationAssetStore.LogoFileName, OrganizationImage.SniffBytes)
                   ?? throw new NotFoundException(noLogo);

        return new OrganizationLogoFile(info.FullName, OrganizationImage.MediaTypeOfContent(head), info);
    }

    private async Task RequireAdministratorAsync(string orgSlug, CancellationToken ct)
    {
        var org = await _datasets.GetRequiredOrganizationAsync(orgSlug, ct: ct);
        if (!await _rights.CanManageAsync(org, await _authManager.GetCurrentUserId(), ct))
            throw new ForbiddenException(AccessDeniedMessages.OrganizationAdminRequired);
    }
}
