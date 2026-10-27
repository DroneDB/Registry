#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Registry.Web.Models.Email;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Email;

/// <summary>The subject and the body of an editable e-mail as text with <c>{{placeholders}}</c>, as stored and edited.</summary>
/// <param name="Subject">The subject.</param>
/// <param name="Body">The body: a blank line starts a paragraph.</param>
public sealed record EmailTemplateText(string Subject, string Body);

/// <summary>An <see cref="EmailTemplateText"/> that passed the checks of <see cref="Parse"/>.</summary>
/// <param name="Subject">The subject, one line.</param>
/// <param name="Body">The body.</param>
public sealed record ParsedEmailTemplate(PlaceholderTemplate Subject, PlaceholderTemplate Body)
{
    /// <summary>Longest subject an administrator can save.</summary>
    public const int MaxSubjectLength = 200;

    /// <summary>Longest body an administrator can save.</summary>
    public const int MaxBodyLength = 16 * 1024;

    /// <summary>
    /// Largest request that carries a text to save, preview or test: the longest subject and body even when a client
    /// escapes every character as <c>\uXXXX</c> (6 bytes each), with room for the JSON around them. A larger request is
    /// refused before it is read.
    /// </summary>
    public const int MaxRequestBytes = 128 * 1024;

    /// <summary>Checks <paramref name="text"/> against the placeholders of <paramref name="template"/> and the limits.</summary>
    /// <exception cref="PlaceholderTemplateException">The text is empty, too long, or has a bad placeholder; the message says which.</exception>
    public static ParsedEmailTemplate Parse(IEditableEmailTemplate template, EmailTemplateText text)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(text);

        var subject = text.Subject ?? string.Empty;
        var body = text.Body ?? string.Empty;

        if (string.IsNullOrWhiteSpace(subject))
            throw new PlaceholderTemplateException("The subject is empty");
        if (subject.Length > MaxSubjectLength)
            throw new PlaceholderTemplateException($"The subject is longer than {MaxSubjectLength} characters");
        if (string.IsNullOrWhiteSpace(body))
            throw new PlaceholderTemplateException("The body is empty");
        if (body.Length > MaxBodyLength)
            throw new PlaceholderTemplateException($"The body is longer than {MaxBodyLength} characters");

        return new ParsedEmailTemplate(
            PlaceholderTemplate.Parse(subject, template.Placeholders, TemplateField.Subject),
            PlaceholderTemplate.Parse(body, template.Placeholders, TemplateField.Body));
    }
}

/// <summary>
/// An e-mail whose text an administrator can change in the Hub (<c>/sys/email/templates</c>). Its built-in text is
/// placeholder text as well, so the default and a custom text go through the same rendering.
/// </summary>
public interface IEditableEmailTemplate
{
    /// <summary>The name in the API and in the template file (e.g. <c>build-notification</c>).</summary>
    string Name { get; }

    /// <summary>What the Hub calls it.</summary>
    string Title { get; }

    /// <summary>The built-in subject and body.</summary>
    EmailTemplateText BuiltInText { get; }

    /// <summary>The placeholders it offers.</summary>
    IReadOnlyList<EmailPlaceholder> Placeholders { get; }

    /// <summary>
    /// The e-mail with <paramref name="text"/> and a built-in sample of the values (preview and test), addressed to
    /// <paramref name="userName"/>, composed by <paramref name="composer"/> like a real one (same layout and branding).
    /// </summary>
    Task<EmailMessage> ComposeSampleAsync(IEmailComposer composer, ParsedEmailTemplate text, string userName,
        IReadOnlyList<EmailAddress> recipients);
}
