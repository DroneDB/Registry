using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Attributes;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.OrganizationAssets;
using Registry.Web.Services.Reports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// The report templates and the annotation schema of an organization, and the logos of the templates. The members of
/// the organization read them and its administrators write them; a reader of a dataset gets them through the dataset.
/// </summary>
[ApiController]
[Route(RoutesHelper.OrganizationPrefix)]
[Produces("application/json")]
public class ReportSettingsController : ControllerBaseEx
{
    private const string OrganizationSettings = "report-settings";
    private const string DatasetSettings = "/" + RoutesHelper.DatasetPrefix + "/report-settings";
    private const string TemplateLogo = OrganizationSettings + "/templates/{templateId}/logo";

    // The settings are a few tens of KB at their limits (20 templates with their texts, the schema)
    private const long MaxSettingsBytes = 256 * 1024;

    // A logo and the framing of its multipart body, with room so that a file just over the logo limit is refused with
    // the message of the logo (400) and only a request of another size is a 413
    private const long MaxLogoRequestBytes = 1024 * 1024;

    private readonly ReportSettingsManager _settings;

    public ReportSettingsController(ReportSettingsManager settings)
    {
        _settings = settings;
    }

    /// <summary>The templates and the annotation schema, for a reader of the dataset.</summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <response code="403">The current user may not read the dataset.</response>
    /// <response code="404">There is no such dataset.</response>
    [HttpGet(DatasetSettings, Name = nameof(ReportSettingsController) + "." + nameof(GetForDataset))]
    [ProducesResponseType(typeof(ReportSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReportSettingsDto>> GetForDataset(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug, CancellationToken ct) =>
        await _settings.GetForDataset(orgSlug, dsSlug, ct);

    /// <summary>The logo of a template, for a reader of the dataset.</summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="templateId">The template.</param>
    /// <response code="200">The image, with the type that was checked when it was uploaded.</response>
    /// <response code="403">The current user may not read the dataset.</response>
    /// <response code="404">There is no such dataset, or the template is not stored or has no logo.</response>
    [HttpGet(DatasetSettings + "/logo/{templateId}", Name = nameof(ReportSettingsController) + "." + nameof(GetDatasetLogo))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDatasetLogo(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string templateId, CancellationToken ct) =>
        Logo(await _settings.GetLogoForDataset(orgSlug, dsSlug, templateId, ct));

    /// <summary>The templates, the annotation schema and the version, for the members of the organization.</summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <response code="403">The current user is not a member of the organization.</response>
    /// <response code="404">There is no such organization.</response>
    [HttpGet(OrganizationSettings, Name = nameof(ReportSettingsController) + "." + nameof(Get))]
    [Authorize]
    [ProducesResponseType(typeof(OrganizationReportSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrganizationReportSettingsDto>> Get(
        [FromRoute, Required] string orgSlug, CancellationToken ct) =>
        await _settings.GetForOrganization(orgSlug, ct);

    /// <summary>
    /// Replaces the templates and the annotation schema of the organization. The logo of a template is not set here: a
    /// <c>logo</c> in the body is ignored, and the logo files of the templates that are no longer there are deleted.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="request">The templates and the schema, with the version they were read at.</param>
    /// <returns>The settings as stored, with the new version.</returns>
    /// <response code="400">A rule of the settings is broken; the message names it.</response>
    /// <response code="403">The current user is not an administrator of the organization.</response>
    /// <response code="404">There is no such organization.</response>
    /// <response code="409">The version of the request is not the stored one: someone else wrote since.</response>
    [HttpPut(OrganizationSettings, Name = nameof(ReportSettingsController) + "." + nameof(Put))]
    [Authorize]
    [RequestSizeLimit(MaxSettingsBytes)]
    [EnforceContentLength(MaxSettingsBytes, "Report settings")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(OrganizationReportSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<OrganizationReportSettingsDto>> Put(
        [FromRoute, Required] string orgSlug,
        [FromBody] ReportSettingsUpdateDto? request, CancellationToken ct) =>
        await _settings.Update(orgSlug, request, ct);

    /// <summary>The logo of a template, for the members of the organization.</summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="templateId">The template.</param>
    /// <response code="200">The image, with the type that was checked when it was uploaded.</response>
    /// <response code="403">The current user is not a member of the organization.</response>
    /// <response code="404">There is no such organization, or the template is not stored or has no logo.</response>
    [HttpGet(TemplateLogo, Name = nameof(ReportSettingsController) + "." + nameof(GetLogo))]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLogo(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string templateId, CancellationToken ct) =>
        Logo(await _settings.GetLogoForOrganization(orgSlug, templateId, ct));

    /// <summary>Sets the logo of a stored template.</summary>
    /// <remarks>
    /// A PNG, JPEG, WebP or SVG image of at most 512 KB, whose type is found from its content. An SVG is stored without
    /// scripts and external references.
    /// </remarks>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="templateId">The template; it must already be stored (the Standard one is).</param>
    /// <param name="file">The image (multipart field <c>file</c>).</param>
    /// <returns>The name of the logo file and the new version of the settings.</returns>
    /// <response code="400">No file, not one of the types, or too large.</response>
    /// <response code="403">The current user is not an administrator of the organization.</response>
    /// <response code="404">There is no such organization, or the template is not stored.</response>
    /// <response code="409">The settings changed while the logo was stored.</response>
    [HttpPut(TemplateLogo, Name = nameof(ReportSettingsController) + "." + nameof(PutLogo))]
    [Authorize]
    [RequestSizeLimit(MaxLogoRequestBytes)]
    [EnforceContentLength(MaxLogoRequestBytes, "Logo upload")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ReportLogoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<ReportLogoDto>> PutLogo(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string templateId,
        IFormFile? file, CancellationToken ct)
    {
        if (file == null)
            throw new BadRequestException("The logo is the multipart field 'file'");

        OrganizationImage.EnsureSize(file.Length, ReportLogoStore.MaxBytes);
        var content = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
            await stream.ReadExactlyAsync(content, ct);

        return await _settings.SetLogo(orgSlug, templateId, content, ct);
    }

    /// <summary>Removes the logo of a stored template.</summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="templateId">The template.</param>
    /// <returns>The new version of the settings.</returns>
    /// <response code="403">The current user is not an administrator of the organization.</response>
    /// <response code="404">There is no such organization, or the template is not stored.</response>
    /// <response code="409">The settings changed while the logo was removed.</response>
    [HttpDelete(TemplateLogo, Name = nameof(ReportSettingsController) + "." + nameof(DeleteLogo))]
    [Authorize]
    [ProducesResponseType(typeof(ReportSettingsVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ReportSettingsVersionDto>> DeleteLogo(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string templateId, CancellationToken ct) =>
        await _settings.RemoveLogo(orgSlug, templateId, ct);

    // The image with the type that was checked at the upload (never one the client or the name says), as an inline
    // image that sandboxes whatever a browser would still run from it; revalidated on every use because the logo of a
    // template can change, and not shared because the logos of a private organization are not for every reader
    private PhysicalFileResult Logo(ReportLogoFile logo)
    {
        Response.Headers.ContentSecurityPolicy = UserContentPolicy.Sandbox;
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "private, no-cache";
        return ValidatedPhysicalFile(logo.Path, logo.MediaType, EntityTags.OfFile(new FileInfo(logo.Path)));
    }
}
