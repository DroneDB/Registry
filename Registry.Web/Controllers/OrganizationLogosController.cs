using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Attributes;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Organizations;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// The logo of an organization. Whoever may read the organization sees it (anyone, when it is public); its
/// administrators set and remove it.
/// </summary>
[ApiController]
[Route(RoutesHelper.OrganizationPrefix + "/logo")]
[Produces("application/json")]
public class OrganizationLogosController : ControllerBaseEx
{
    // A logo and the framing of its multipart body: a request of another size is refused before it is read
    private const long MaxRequestBytes = OrganizationLogos.MaxBytes + 64 * 1024;

    private readonly OrganizationLogos _logos;

    public OrganizationLogosController(OrganizationLogos logos)
    {
        _logos = logos;
    }

    /// <summary>The logo of the organization.</summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="v">The version the URL of the logo names (<c>logoUrl</c> of the organization): when it is the current one the image may be kept without asking.</param>
    /// <response code="200">The image, with the type found from its content.</response>
    /// <response code="304">The client has this image.</response>
    /// <response code="403">The current user may not read the organization.</response>
    /// <response code="404">There is no such organization, or it has no logo.</response>
    [HttpGet(Name = nameof(OrganizationLogosController) + "." + nameof(Get))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get([FromRoute, Required] string orgSlug, [FromQuery] string? v = null,
        CancellationToken ct = default) =>
        Serve(await _logos.OpenAsync(orgSlug, ct), v);

    /// <summary>The logo of the organization, for a reader of one of its datasets.</summary>
    /// <remarks>A reader of a public dataset of a private organization may not read the organization itself.</remarks>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="v">The version the URL of the logo names (<c>organizationLogoUrl</c> of the report settings of the dataset).</param>
    /// <response code="200">The image, with the type found from its content.</response>
    /// <response code="304">The client has this image.</response>
    /// <response code="403">The current user may not read the dataset.</response>
    /// <response code="404">There is no such dataset, or the organization has no logo.</response>
    [HttpGet("/" + RoutesHelper.DatasetPrefix + "/organization-logo", Name = nameof(OrganizationLogosController) + "." + nameof(GetForDataset))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetForDataset([FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromQuery] string? v = null, CancellationToken ct = default) =>
        Serve(await _logos.OpenForDatasetAsync(orgSlug, dsSlug, ct), v);

    private PhysicalFileResult Serve(OrganizationLogoFile logo, string? v)
    {
        // The image with the type its content has (never one the client or a name says), as an inline image that
        // sandboxes whatever a browser would still run from it. Not for shared caches: the logo of a private
        // organization is not for every reader. A URL with the current version names these bytes for good; any other
        // may show another image tomorrow and is asked about each time
        Response.Headers.ContentSecurityPolicy = UserContentPolicy.Sandbox;
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = HashedContentCachePolicy.CacheControlFor(logo.Version, v);
        return ValidatedPhysicalFile(logo.Path, logo.MediaType, EntityTags.OfFile(logo.Info));
    }

    /// <summary>Sets the logo of the organization, replacing the one it has.</summary>
    /// <remarks>
    /// A PNG, JPEG, WebP or SVG image of at most 256 KB, whose type is found from its content. An SVG is stored without
    /// scripts and external references.
    /// </remarks>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="file">The image (multipart field <c>file</c>).</param>
    /// <returns>Where the logo is now.</returns>
    /// <response code="400">No file, or not one of the types.</response>
    /// <response code="403">The current user is not an administrator of the organization.</response>
    /// <response code="404">There is no such organization.</response>
    /// <response code="413">The file is over 256 KB.</response>
    [HttpPut(Name = nameof(OrganizationLogosController) + "." + nameof(Put))]
    [Authorize]
    [RequestSizeLimit(MaxRequestBytes)]
    [EnforceContentLength(MaxRequestBytes, "Logo upload")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(OrganizationLogoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<OrganizationLogoDto>> Put([FromRoute, Required] string orgSlug, IFormFile? file,
        CancellationToken ct)
    {
        if (file == null)
            throw new BadRequestException("The logo is the multipart field 'file'");

        // A file over the limit is a payload too large, whatever the framing around it came to
        if (file.Length > OrganizationLogos.MaxBytes)
            throw new PayloadTooLargeException($"The logo can be at most {OrganizationLogos.MaxBytes / 1024} KB");

        var content = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
            await stream.ReadExactlyAsync(content, ct);

        return new OrganizationLogoDto { LogoUrl = await _logos.SetAsync(orgSlug, content, ct) };
    }

    /// <summary>Removes the logo of the organization.</summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <response code="204">The organization has no logo now (it had none, or the one it had is gone).</response>
    /// <response code="403">The current user is not an administrator of the organization.</response>
    /// <response code="404">There is no such organization.</response>
    [HttpDelete(Name = nameof(OrganizationLogosController) + "." + nameof(Delete))]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete([FromRoute, Required] string orgSlug, CancellationToken ct)
    {
        await _logos.RemoveAsync(orgSlug, ct);
        return NoContent();
    }
}
