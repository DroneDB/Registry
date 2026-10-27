#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Registry.Web.Exceptions;

namespace Registry.Web.Services.Email;

/// <summary>
/// The editable e-mails and their current text: the one an administrator saved, or the built-in one. A saved text is
/// checked again when it is read, so a text that a newer version no longer accepts (a placeholder that went away)
/// falls back to the built-in text instead of failing the e-mail.
/// </summary>
public sealed class EditableEmailTemplates
{
    private readonly IReadOnlyList<IEditableEmailTemplate> _templates;
    private readonly EditableEmailTemplateStore _store;
    private readonly ILogger<EditableEmailTemplates> _logger;

    public EditableEmailTemplates(IEnumerable<IEditableEmailTemplate> templates, EditableEmailTemplateStore store,
        ILogger<EditableEmailTemplates> logger)
    {
        _templates = [.. (templates ?? throw new ArgumentNullException(nameof(templates))).OrderBy(t => t.Name, StringComparer.Ordinal)];
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Every editable e-mail, by name.</summary>
    public IReadOnlyList<IEditableEmailTemplate> All => _templates;

    /// <summary>The editable e-mail named <paramref name="name"/>.</summary>
    /// <exception cref="NotFoundException">There is none.</exception>
    public IEditableEmailTemplate Find(string name) =>
        _templates.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal)) ??
        throw new NotFoundException($"There is no e-mail template named '{name}'");

    /// <summary>The text <paramref name="template"/> is sent with, and whether it is the built-in one.</summary>
    public (EmailTemplateText Text, ParsedEmailTemplate Parsed, bool IsDefault) Current(IEditableEmailTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (_store.Get(template.Name) is { } saved)
        {
            try
            {
                return (saved, ParsedEmailTemplate.Parse(template, saved), false);
            }
            catch (PlaceholderTemplateException ex)
            {
                _logger.LogWarning("The saved text of the e-mail template {Template} is not valid ({Reason}): the built-in text is used",
                    template.Name, ex.Message);
            }
        }

        return (template.BuiltInText, ParsedEmailTemplate.Parse(template, template.BuiltInText), true);
    }

    /// <summary><paramref name="text"/> checked for <paramref name="template"/>.</summary>
    /// <exception cref="BadRequestException">The text is not valid; the message names the problem.</exception>
    public static ParsedEmailTemplate Validate(IEditableEmailTemplate template, EmailTemplateText text)
    {
        try
        {
            return ParsedEmailTemplate.Parse(template, text);
        }
        catch (PlaceholderTemplateException ex)
        {
            throw new BadRequestException(ex.Message, noRetry: true, ex);
        }
    }

    /// <summary>Checks <paramref name="text"/> and saves it as the text of <paramref name="template"/>.</summary>
    /// <exception cref="BadRequestException">The text is not valid.</exception>
    public void Save(IEditableEmailTemplate template, EmailTemplateText text)
    {
        Validate(template, text);
        _store.Set(template.Name, text);
    }

    /// <summary>Removes the saved text of <paramref name="template"/>: the built-in text applies again.</summary>
    public void Reset(IEditableEmailTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        _store.Remove(template.Name);
    }
}
