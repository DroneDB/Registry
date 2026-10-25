#nullable enable
using System;
using System.Globalization;

namespace Registry.Web.Services.Email.Templates;

/// <summary>What the test e-mail shows.</summary>
/// <param name="RequestedBy">The administrator who asked for it.</param>
/// <param name="SentAt">When it was asked for.</param>
public sealed record TestEmailModel(string RequestedBy, DateTimeOffset SentAt);

/// <summary>The e-mail an administrator sends to check the SMTP settings (<c>POST /sys/email/test</c>).</summary>
public sealed class TestEmailTemplate : EmailTemplate<TestEmailModel, TestEmail>
{
    /// <summary>The template (it has no state).</summary>
    public static readonly TestEmailTemplate Instance = new();

    private TestEmailTemplate()
    {
    }

    /// <inheritdoc />
    public override string Subject(TestEmailModel model, EmailBranding branding) => $"{branding.AppName} test e-mail";

    /// <inheritdoc />
    public override string Text(TestEmailModel model, EmailBranding branding) =>
        $"""
         This is a test e-mail from {branding.AppName}.

         You received it, so the outgoing e-mail settings work.

         Sent at the request of {model.RequestedBy} on {model.SentAt.ToString("u", CultureInfo.InvariantCulture)}.
         """;
}
