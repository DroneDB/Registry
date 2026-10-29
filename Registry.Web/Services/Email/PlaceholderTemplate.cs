using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Registry.Web.Services.Email;

/// <summary>A value an editable e-mail can insert with <c>{{name}}</c>.</summary>
/// <param name="Name">The name between the braces.</param>
/// <param name="Description">What it inserts, for the editor.</param>
/// <param name="InSubject">Whether the subject may use it (a link or a list cannot go in a subject line).</param>
/// <param name="IsBlock">Whether it is a block of its own (a list, a table), which ends the paragraph around it.</param>
public sealed record EmailPlaceholder(string Name, string Description, bool InSubject, bool IsBlock = false);

/// <summary>The part of an e-mail a template text is for: the names appear in the validation messages.</summary>
public enum TemplateField
{
    Subject,
    Body
}

/// <summary>A piece of a parsed template: text as written, or a placeholder.</summary>
public abstract record TemplatePart;

/// <summary>Text as the administrator wrote it: written as text everywhere (encoded in HTML).</summary>
public sealed record TemplateText(string Text) : TemplatePart;

/// <summary>A <c>{{name}}</c> placeholder.</summary>
public sealed record TemplateValue(string Name) : TemplatePart;

/// <summary>A block of a template body: a paragraph or a block placeholder.</summary>
public abstract record TemplateBlock;

/// <summary>A paragraph (the text between blank lines), as its lines.</summary>
public sealed record TemplateParagraph(IReadOnlyList<IReadOnlyList<TemplatePart>> Lines) : TemplateBlock
{
    /// <summary>The placeholders the paragraph uses.</summary>
    public IEnumerable<string> Names => Lines.SelectMany(line => line).OfType<TemplateValue>().Select(v => v.Name);

    /// <summary>The paragraph is one placeholder alone (a link that may then be shown as a button).</summary>
    public string? Alone => Lines is [[TemplateValue value]] ? value.Name : null;
}

/// <summary>A block placeholder (a list or a table) on its own.</summary>
public sealed record TemplateBlockValue(string Name) : TemplateBlock;

/// <summary>A template text that is not valid; the message names the bad token, for the administrator.</summary>
public sealed class PlaceholderTemplateException : FormatException
{
    public PlaceholderTemplateException(string message) : base(message)
    {
    }
}

/// <summary>
/// A text with <c>{{name}}</c> placeholders, as an administrator edits it: text segments and placeholder segments. The
/// built-in default of a template is such a text too, so the default and a custom text are rendered the same way.
/// <para>
/// The body has a structure of its own: a blank line starts a paragraph, a single line break is a line break, and a
/// block placeholder stands as a block. The plain-text and the HTML part are made from the same <see cref="Blocks"/>,
/// so the two parts say the same. A paragraph that uses a placeholder with no value (a link on an instance without a
/// public URL) is left out of both parts, with the words around it.
/// </para>
/// </summary>
public sealed partial class PlaceholderTemplate
{
    private const string Open = "{{";
    private const string Close = "}}";

    private readonly HashSet<string> _blocks;

    private PlaceholderTemplate(IReadOnlyList<TemplatePart> parts, HashSet<string> blocks)
    {
        Parts = parts;
        _blocks = blocks;
    }

    /// <summary>The text and placeholder segments, in order.</summary>
    public IReadOnlyList<TemplatePart> Parts { get; }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*\z")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Parses <paramref name="text"/> with the placeholders in <paramref name="allowed"/>.</summary>
    /// <exception cref="PlaceholderTemplateException">
    /// A <c>{{</c> is not closed, a placeholder is not a name, is not one of <paramref name="allowed"/>, or cannot be used
    /// in the subject.
    /// </exception>
    public static PlaceholderTemplate Parse(string text, IReadOnlyCollection<EmailPlaceholder> allowed, TemplateField field)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(allowed);

        var known = allowed.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var parts = new List<TemplatePart>();
        var where = field == TemplateField.Subject ? "the subject" : "the body";
        var position = 0;

        while (position < text.Length)
        {
            var start = text.IndexOf(Open, position, StringComparison.Ordinal);
            if (start < 0)
            {
                parts.Add(new TemplateText(text[position..]));
                break;
            }

            if (start > position)
                parts.Add(new TemplateText(text[position..start]));

            var end = text.IndexOf(Close, start + Open.Length, StringComparison.Ordinal);
            if (end < 0)
                throw new PlaceholderTemplateException(
                    $"Unclosed placeholder \"{Excerpt(text, start)}\" in {where}: a placeholder ends with {Close}");

            var name = text[(start + Open.Length)..end].Trim();
            if (!NamePattern().IsMatch(name))
                throw new PlaceholderTemplateException(
                    $"Invalid placeholder {Open}{text[(start + Open.Length)..end]}{Close} in {where}: a placeholder is a name such as {Open}appName{Close}");

            if (!known.TryGetValue(name, out var placeholder))
                throw new PlaceholderTemplateException(
                    $"Unknown placeholder {Open}{name}{Close} in {where}; the placeholders are {List(allowed, field)}");

            if (field == TemplateField.Subject && !placeholder.InSubject)
                throw new PlaceholderTemplateException(
                    $"The placeholder {Open}{name}{Close} can only be used in the body, not in the subject");

            parts.Add(new TemplateValue(name));
            position = end + Close.Length;
        }

        return new PlaceholderTemplate(parts,
            allowed.Where(p => p.IsBlock).Select(p => p.Name).ToHashSet(StringComparer.Ordinal));
    }

    private static string Excerpt(string text, int start)
    {
        var excerpt = text[start..].ReplaceLineEndings(" ");
        return excerpt.Length <= 24 ? excerpt : excerpt[..24] + "…";
    }

    private static string List(IReadOnlyCollection<EmailPlaceholder> allowed, TemplateField field) =>
        string.Join(", ", allowed.Where(p => field == TemplateField.Body || p.InSubject).Select(p => Open + p.Name + Close));

    /// <summary>
    /// The text on one line (a subject): the values inserted (none for a placeholder without one), line breaks and runs
    /// of spaces made one space.
    /// </summary>
    public string RenderLine(Func<string, string?> value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var text = new StringBuilder();
        foreach (var part in Parts)
            text.Append(part switch
            {
                TemplateText t => t.Text,
                TemplateValue v => value(v.Name),
                _ => null
            });

        return Whitespace().Replace(text.ToString(), " ").Trim();
    }

    /// <summary>
    /// The body as paragraphs and block placeholders, without the ones that use a placeholder for which
    /// <paramref name="hasValue"/> is false.
    /// </summary>
    public IReadOnlyList<TemplateBlock> Blocks(Func<string, bool> hasValue)
    {
        ArgumentNullException.ThrowIfNull(hasValue);

        return AllBlocks()
            .Where(block => block switch
            {
                TemplateParagraph paragraph => paragraph.Names.All(hasValue),
                TemplateBlockValue value => hasValue(value.Name),
                _ => false
            })
            .ToList();
    }

    /// <summary>
    /// The plain-text body: the paragraphs of <see cref="Blocks"/> with the values inserted, a blank line between blocks.
    /// <paramref name="value"/> gives the text of a placeholder (several lines for a block), null when it has none.
    /// </summary>
    public string RenderText(Func<string, string?> value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var blocks = Blocks(name => value(name) != null).Select(block => block switch
        {
            TemplateParagraph paragraph => string.Join("\n", paragraph.Lines.Select(line =>
                string.Concat(line.Select(part => part switch
                {
                    TemplateText t => t.Text,
                    TemplateValue v => value(v.Name),
                    _ => null
                })))),
            TemplateBlockValue standalone => value(standalone.Name)!.TrimEnd(),
            _ => string.Empty
        });

        return string.Join("\n\n", blocks);
    }

    private List<TemplateBlock> AllBlocks()
    {
        var blocks = new List<TemplateBlock>();
        var paragraph = new List<IReadOnlyList<TemplatePart>>();
        var line = new List<TemplatePart>();

        void EndParagraph()
        {
            if (!IsBlank(line))
                paragraph.Add(Trimmed(line));
            line = [];

            if (paragraph.Count > 0)
                blocks.Add(new TemplateParagraph(paragraph));
            paragraph = [];
        }

        void EndLine()
        {
            // A blank line ends the paragraph; any other line break is a line break inside it
            if (IsBlank(line))
                EndParagraph();
            else
                paragraph.Add(Trimmed(line));
            line = [];
        }

        foreach (var part in Parts)
        {
            switch (part)
            {
                case TemplateValue value when _blocks.Contains(value.Name):
                    EndParagraph();
                    blocks.Add(new TemplateBlockValue(value.Name));
                    break;
                case TemplateValue value:
                    line.Add(value);
                    break;
                case TemplateText text:
                    var pieces = text.Text.ReplaceLineEndings("\n").Split('\n');
                    for (var i = 0; i < pieces.Length; i++)
                    {
                        if (i > 0)
                            EndLine();
                        if (pieces[i].Length > 0)
                            line.Add(new TemplateText(pieces[i]));
                    }

                    break;
            }
        }

        EndParagraph();
        return blocks;
    }

    private static bool IsBlank(List<TemplatePart> line) =>
        line.All(part => part is TemplateText text && string.IsNullOrWhiteSpace(text.Text));

    // The spaces at the start and the end of a line are layout of the editor, not of the e-mail
    private static List<TemplatePart> Trimmed(List<TemplatePart> line)
    {
        var parts = new List<TemplatePart>(line);
        if (parts[0] is TemplateText first)
            parts[0] = new TemplateText(first.Text.TrimStart());
        if (parts[^1] is TemplateText last)
            parts[^1] = new TemplateText(last.Text.TrimEnd());

        return parts.Where(part => part is not TemplateText { Text.Length: 0 }).ToList();
    }
}
