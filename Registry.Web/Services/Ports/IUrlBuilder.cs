#nullable disable
using System.Collections.Generic;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// The absolute URLs Registry gives its resources (split from the former utilities service): the public
/// host (the external URL override, else the request's), the dataset URLs.
/// </summary>
public interface IUrlBuilder
{
    /// <summary>
    /// The public base URL, e.g. <c>https://hub.example</c>; with <paramref name="useDdbScheme"/> the <c>ddb://</c>
    /// (<c>ddb+unsafe://</c> over HTTP) form the DroneDB client opens.
    /// </summary>
    string GetLocalHost(bool useDdbScheme = false);

    /// <summary>
    /// The API URL of a dataset (<c>{host}/orgs/{org}/ds/{ds}</c>), or with <paramref name="useDdbScheme"/> its
    /// DroneDB client URL (<c>ddb://{host}/{org}/{ds}</c>).
    /// </summary>
    string GenerateDatasetUrl(Dataset dataset, bool useDdbScheme = false);

    /// <summary>
    /// The artifacts of <paramref name="build"/> as links: the role as libddb names it, the build-file URL
    /// (a directory artifact's descriptor), the served media type. Empty for a build with no artifact or a build
    /// that is not the entry's own (a sidecar's).
    /// </summary>
    IReadOnlyList<ArtifactLinkDto> ArtifactLinks(string orgSlug, string dsSlug, EntryBuildArtifacts build);
}
