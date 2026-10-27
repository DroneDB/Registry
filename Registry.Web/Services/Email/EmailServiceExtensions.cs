#nullable enable
using System;
using Registry.Web.Exceptions;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Email;

/// <summary>The checks the e-mail endpoints share.</summary>
public static class EmailServiceExtensions
{
    /// <summary>Throws when e-mail is off: an endpoint that sends now must say so instead of dropping the e-mail.</summary>
    /// <exception cref="BadRequestException">E-mail is disabled.</exception>
    public static void EnsureEnabled(this IEmailService email)
    {
        ArgumentNullException.ThrowIfNull(email);

        if (!email.IsEnabled)
            throw new BadRequestException("E-mail is disabled: configure AppSettings:Smtp and set Enabled to true", noRetry: true);
    }
}
