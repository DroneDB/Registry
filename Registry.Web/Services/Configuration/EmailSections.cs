#nullable disable
using System;
using System.Collections.Generic;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using static Registry.Web.Services.Configuration.ConfigurationFields;

namespace Registry.Web.Services.Configuration;

/// <summary>The admin configuration section of the outgoing e-mail server (<c>AppSettings:Smtp</c>).</summary>
public sealed class EmailSections : IConfigurationSectionBuilder
{
    private readonly ConfigurationView _view;

    public EmailSections(ConfigurationView view)
    {
        _view = view;
    }

    public IEnumerable<PlacedSection> Build() =>
    [
        new(ConfigurationSection.Email, BuildSmtpSection())
    ];

    private ConfigurationSectionDataDto BuildSmtpSection()
    {
        var smtp = _view.ConfiguredOrNull<SmtpSettings>();
        var shipped = _view.Shipped<SmtpSettings>();

        // The section name is the key of the file (AppSettings:Smtp): the editor writes the fields under it
        var section = Section("Smtp", "E-mail (SMTP)",
            "Outgoing e-mail server. While disabled, every e-mail is logged and dropped. Test it with POST /sys/email/test.",
            [
                BoolField("Enabled", "E-mail Enabled",
                    "Sends e-mail through the server below.",
                    smtp?.Enabled ?? false, shipped.Enabled),

                StringField("Host", "SMTP Host",
                    "SMTP server host name (e.g., 'smtp.example.com').",
                    smtp?.Host, shipped.Host),

                NumberField("Port", "SMTP Port",
                    "587 for STARTTLS, 465 for SSL on connect, 25 for a local relay.",
                    smtp?.Port ?? shipped.Port, shipped.Port, minValue: 1, maxValue: 65535),

                EnumField("Security", "Connection Security",
                    "StartTls: upgrade with STARTTLS (required). SslOnConnect: TLS from the start. None: no encryption (local relay only).",
                    (smtp?.Security ?? shipped.Security).ToString(), shipped.Security.ToString(),
                    Enum.GetNames<SmtpSecurity>()),

                BoolField("ValidateServerCertificate", "Validate Server Certificate",
                    "Validate the server certificate chain. Disable only for a test server with a self-signed certificate (it must still match the host name).",
                    smtp?.ValidateServerCertificate ?? shipped.ValidateServerCertificate, shipped.ValidateServerCertificate),

                StringField("UserName", "User Name",
                    "SMTP account user name. Leave empty (with the password) for no authentication.",
                    smtp?.UserName, shipped.UserName),

                SensitiveField("Password", "Password",
                    "SMTP account password. Prefer the AppSettings__Smtp__Password environment variable.",
                    smtp?.Password, shipped.Password),

                StringField("FromAddress", "From Address",
                    "Address the e-mails are sent from.",
                    smtp?.FromAddress, shipped.FromAddress),

                StringField("FromName", "From Name",
                    "Display name of the sender. Empty: the Hub application name.",
                    smtp?.FromName, shipped.FromName),

                StringField("ReplyTo", "Reply-To Address",
                    "Address replies go to, when it is not the sender's.",
                    smtp?.ReplyTo, shipped.ReplyTo),

                NumberField("TimeoutSeconds", "Timeout",
                    "Timeout of each SMTP operation.",
                    smtp?.TimeoutSeconds ?? shipped.TimeoutSeconds, shipped.TimeoutSeconds, minValue: 1, unit: "seconds")
            ]);

        // Active when e-mail is on, like the LDAP section
        section.IsActive = smtp?.Enabled ?? false;
        return section;
    }
}
