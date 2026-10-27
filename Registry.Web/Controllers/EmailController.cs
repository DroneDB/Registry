#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Models.Email;
using Registry.Web.Services.Email;
using Registry.Web.Services.Email.Templates;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Controllers;

/// <summary>The outgoing e-mail of the instance (route names of <see cref="SystemRoutes"/>). Administrators only.</summary>
[Authorize]
[ApiController]
[Route(RoutesHelper.SystemRadix)]
[Produces("application/json")]
public class EmailController : ControllerBaseEx
{
    private readonly IEmailService _email;
    private readonly IAuthManager _auth;

    public EmailController(IEmailService email, IAuthManager auth)
    {
        _email = email;
        _auth = auth;
    }

    /// <summary>
    /// Sends a test e-mail now, with the configured SMTP settings, so an administrator can check them.
    /// </summary>
    /// <param name="request">The recipient.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">The mail server accepted the e-mail.</response>
    /// <response code="400">E-mail is disabled, or the address is not valid.</response>
    /// <response code="502">The mail server refused the e-mail or could not be reached; the message says why.</response>
    [HttpPost("email/test", Name = SystemRoutes.Prefix + nameof(SendTestEmail))]
    [Authorize(Policy = Policies.Admin)]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> SendTestEmail([FromBody] TestEmailRequestDto request, CancellationToken ct)
    {
        _email.EnsureEnabled();

        var model = new TestEmailModel(await _auth.SafeGetCurrentUserName(), DateTimeOffset.UtcNow);
        await _email.SendNowAsync(TestEmailTemplate.Instance, model, [new EmailAddress(request.To.Trim())], ct);

        return NoContent();
    }
}
