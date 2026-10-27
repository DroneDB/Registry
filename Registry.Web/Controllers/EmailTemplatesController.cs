#nullable enable
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Attributes;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Email;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Controllers;

/// <summary>
/// The e-mails whose text administrators can change (route names of <see cref="SystemRoutes"/>): the text with
/// <c>{{placeholders}}</c>, a preview, a test send and the reset to the built-in text. Administrators only.
/// </summary>
[Authorize]
[ApiController]
[Route(RoutesHelper.SystemRadix)]
[Produces("application/json")]
public class EmailTemplatesController : ControllerBaseEx
{
    private readonly EditableEmailTemplates _templates;
    private readonly IEmailComposer _composer;
    private readonly IEmailService _email;
    private readonly IAuthManager _auth;
    private readonly IUserRecipients _recipients;

    public EmailTemplatesController(EditableEmailTemplates templates, IEmailComposer composer, IEmailService email,
        IAuthManager auth, IUserRecipients recipients)
    {
        _templates = templates;
        _composer = composer;
        _email = email;
        _auth = auth;
        _recipients = recipients;
    }

    /// <summary>The editable e-mails.</summary>
    /// <returns>Their names and titles, and whether each is sent with its built-in text.</returns>
    [HttpGet("email/templates", Name = SystemRoutes.Prefix + nameof(GetEmailTemplates))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public ActionResult<EmailTemplateSummaryDto[]> GetEmailTemplates() =>
        _templates.All.Select(template => new EmailTemplateSummaryDto
        {
            Name = template.Name,
            Title = template.Title,
            IsDefault = _templates.Current(template).IsDefault
        }).ToArray();

    /// <summary>An editable e-mail: its current text, its built-in text and its placeholders.</summary>
    /// <param name="name">The template name (e.g. <c>build-notification</c>).</param>
    /// <response code="404">There is no template with that name.</response>
    [HttpGet("email/templates/{name}", Name = SystemRoutes.Prefix + nameof(GetEmailTemplate))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<EmailTemplateDto> GetEmailTemplate([FromRoute] string name) => Describe(_templates.Find(name));

    /// <summary>
    /// Saves the text of an editable e-mail. Every placeholder must be one the template offers, and the subject may
    /// only use the ones allowed there.
    /// </summary>
    /// <param name="name">The template name.</param>
    /// <param name="request">The subject and the body.</param>
    /// <returns>The template with the saved text.</returns>
    /// <response code="400">The text is not valid (an empty or too long field, an unknown or unclosed placeholder, a body-only placeholder in the subject); the message names it.</response>
    /// <response code="404">There is no template with that name.</response>
    [HttpPut("email/templates/{name}", Name = SystemRoutes.Prefix + nameof(PutEmailTemplate))]
    [RequestSizeLimit(ParsedEmailTemplate.MaxRequestBytes)]
    [EnforceContentLength(ParsedEmailTemplate.MaxRequestBytes, "E-mail template")]
    [Authorize(Policy = Policies.Admin)]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    public ActionResult<EmailTemplateDto> PutEmailTemplate([FromRoute] string name, [FromBody] EmailTemplateTextDto request)
    {
        var template = _templates.Find(name);
        _templates.Save(template, TextOf(request));
        return Describe(template);
    }

    /// <summary>Removes the saved text of an editable e-mail: it is sent with its built-in text again.</summary>
    /// <param name="name">The template name.</param>
    /// <returns>The template with the built-in text.</returns>
    /// <response code="404">There is no template with that name.</response>
    [HttpDelete("email/templates/{name}", Name = SystemRoutes.Prefix + nameof(ResetEmailTemplate))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<EmailTemplateDto> ResetEmailTemplate([FromRoute] string name)
    {
        var template = _templates.Find(name);
        _templates.Reset(template);
        return Describe(template);
    }

    /// <summary>
    /// Renders a text (not saved) with a sample, through the real layout and branding: what a user would receive.
    /// </summary>
    /// <param name="name">The template name.</param>
    /// <param name="request">The subject and the body to try.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The subject, the plain-text part and the whole HTML document.</returns>
    /// <response code="400">The text is not valid; the message names the problem.</response>
    /// <response code="404">There is no template with that name.</response>
    [HttpPost("email/templates/{name}/preview", Name = SystemRoutes.Prefix + nameof(PreviewEmailTemplate))]
    [RequestSizeLimit(ParsedEmailTemplate.MaxRequestBytes)]
    [EnforceContentLength(ParsedEmailTemplate.MaxRequestBytes, "E-mail template")]
    [Authorize(Policy = Policies.Admin)]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<EmailTemplatePreviewDto>> PreviewEmailTemplate([FromRoute] string name,
        [FromBody] EmailTemplateTextDto request, CancellationToken ct)
    {
        var template = _templates.Find(name);
        var text = EditableEmailTemplates.Validate(template, TextOf(request));

        var message = await template.ComposeSampleAsync(_composer, text, await _auth.SafeGetCurrentUserName(), []);

        return new EmailTemplatePreviewDto
        {
            Subject = message.Subject,
            Text = message.TextBody,
            Html = message.HtmlBody ?? string.Empty
        };
    }

    /// <summary>
    /// Sends a text (not saved) with a sample to the calling administrator now, like <c>POST /sys/email/test</c>.
    /// </summary>
    /// <param name="name">The template name.</param>
    /// <param name="request">The subject and the body to try.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">The mail server accepted the e-mail.</response>
    /// <response code="400">E-mail is disabled, the administrator has no valid address, or the text is not valid.</response>
    /// <response code="404">There is no template with that name.</response>
    /// <response code="502">The mail server refused the e-mail or could not be reached; the message says why.</response>
    [HttpPost("email/templates/{name}/test", Name = SystemRoutes.Prefix + nameof(SendEmailTemplateTest))]
    [RequestSizeLimit(ParsedEmailTemplate.MaxRequestBytes)]
    [EnforceContentLength(ParsedEmailTemplate.MaxRequestBytes, "E-mail template")]
    [Authorize(Policy = Policies.Admin)]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> SendEmailTemplateTest([FromRoute] string name, [FromBody] EmailTemplateTextDto request,
        CancellationToken ct)
    {
        _email.EnsureEnabled();

        var template = _templates.Find(name);
        var text = EditableEmailTemplates.Validate(template, TextOf(request));

        var to = await _recipients.FindAsync(await _auth.GetCurrentUserId(), ct) ??
                 throw new BadRequestException("Your account has no valid e-mail address to send the test to", noRetry: true);

        var message = await template.ComposeSampleAsync(_composer, text, to.DisplayName ?? await _auth.SafeGetCurrentUserName(), [to]);
        await _email.SendNowAsync(message, ct);

        return NoContent();
    }

    private static EmailTemplateText TextOf(EmailTemplateTextDto? request) =>
        new(request?.Subject ?? string.Empty, request?.Body ?? string.Empty);

    private EmailTemplateDto Describe(IEditableEmailTemplate template)
    {
        var current = _templates.Current(template);

        return new EmailTemplateDto
        {
            Name = template.Name,
            Title = template.Title,
            Subject = current.Text.Subject,
            Body = current.Text.Body,
            IsDefault = current.IsDefault,
            DefaultSubject = template.BuiltInText.Subject,
            DefaultBody = template.BuiltInText.Body,
            Placeholders =
            [
                .. template.Placeholders.Select(p => new EmailTemplatePlaceholderDto
                {
                    Name = p.Name, Description = p.Description, InSubject = p.InSubject
                })
            ]
        };
    }
}
