namespace Registry.Web.Models.DTO;

/// <summary>An editable e-mail in the list of <c>GET /sys/email/templates</c>.</summary>
public sealed class EmailTemplateSummaryDto
{
    /// <summary>The name in the API (e.g. <c>build-notification</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What the Hub calls it.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Whether it is sent with its built-in text (no text was saved).</summary>
    public bool IsDefault { get; set; }
}

/// <summary>A placeholder an editable e-mail offers.</summary>
public sealed class EmailTemplatePlaceholderDto
{
    /// <summary>The name, written <c>{{name}}</c> in the text.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What it inserts.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Whether the subject may use it.</summary>
    public bool InSubject { get; set; }
}

/// <summary>
/// An editable e-mail with its current text (<c>GET</c>, <c>PUT</c> and <c>DELETE /sys/email/templates/{name}</c>): the
/// saved one, or the built-in one when <see cref="IsDefault"/>.
/// </summary>
public sealed class EmailTemplateDto
{
    /// <summary>The name in the API.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What the Hub calls it.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The current subject.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The current body.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Whether the current text is the built-in one.</summary>
    public bool IsDefault { get; set; }

    /// <summary>The built-in subject (what "reset to default" gives).</summary>
    public string DefaultSubject { get; set; } = string.Empty;

    /// <summary>The built-in body.</summary>
    public string DefaultBody { get; set; } = string.Empty;

    /// <summary>The placeholders the text can use.</summary>
    public EmailTemplatePlaceholderDto[] Placeholders { get; set; } = [];
}

/// <summary>
/// The text of an editable e-mail as the editor holds it: the body of <c>PUT /sys/email/templates/{name}</c>, and of its
/// <c>preview</c> and <c>test</c> (unsaved text).
/// </summary>
public sealed class EmailTemplateTextDto
{
    /// <summary>The subject, with placeholders.</summary>
    public string? Subject { get; set; }

    /// <summary>The body, with placeholders: a blank line starts a paragraph.</summary>
    public string? Body { get; set; }
}

/// <summary>An editable e-mail rendered with a sample (<c>POST /sys/email/templates/{name}/preview</c>).</summary>
public sealed class EmailTemplatePreviewDto
{
    /// <summary>The rendered subject.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The plain-text part.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The HTML part: the whole document, in the layout and branding of the instance.</summary>
    public string Html { get; set; } = string.Empty;
}
