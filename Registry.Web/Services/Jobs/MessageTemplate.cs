using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Registry.Web.Services.Jobs;

/// <summary>
/// A message template as the logger takes it (<c>"Removed {Count} entries"</c>): the named holes
/// in order and the text rendered with positional arguments. Holes are <c>{Name}</c>,
/// <c>{Name:format}</c>, <c>{Name,alignment}</c> and the Serilog prefixes <c>{@Name}</c>/<c>{$Name}</c>;
/// <c>{{</c> and <c>}}</c> are literal braces. Values are formatted with the invariant culture, as
/// Microsoft.Extensions.Logging renders them.
/// </summary>
public static class MessageTemplate
{
    /// <summary>The hole names of <paramref name="template"/>, in order (prefixes, format and alignment removed).</summary>
    public static IReadOnlyList<string> HoleNames(string template)
    {
        var names = new List<string>();
        Walk(template, null, hole => names.Add(hole.Name));
        return names;
    }

    /// <summary>
    /// <paramref name="template"/> with each hole replaced by the argument at its position; a hole
    /// with no argument stays as written, a null argument renders as <c>(null)</c>.
    /// </summary>
    public static string Render(string template, IReadOnlyList<object?>? args)
    {
        var sb = new StringBuilder(template.Length + 16);
        var index = 0;
        Walk(template, sb, hole =>
        {
            if (args == null || index >= args.Count)
            {
                sb.Append(hole.Raw);
                index++;
                return;
            }

            sb.Append(Format(args[index++], hole.Format, hole.Alignment));
        });
        return sb.ToString();
    }

    private readonly record struct Hole(string Raw, string Name, string? Format, int? Alignment);

    // Literal text goes to sb (when given); each hole goes to onHole
    private static void Walk(string template, StringBuilder? sb, Action<Hole> onHole)
    {
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    sb?.Append('{');
                    i++;
                    continue;
                }

                var end = template.IndexOf('}', i + 1);
                if (end < 0)
                {
                    sb?.Append(template, i, template.Length - i);
                    return;
                }

                var raw = template.Substring(i, end - i + 1);
                onHole(Parse(raw));
                i = end;
            }
            else if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                sb?.Append('}');
                i++;
            }
            else
            {
                sb?.Append(c);
            }
        }
    }

    private static Hole Parse(string raw)
    {
        var body = raw[1..^1];
        string? format = null;
        int? alignment = null;

        var colon = body.IndexOf(':');
        if (colon >= 0)
        {
            format = body[(colon + 1)..];
            body = body[..colon];
        }

        var comma = body.IndexOf(',');
        if (comma >= 0)
        {
            if (int.TryParse(body[(comma + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var a))
                alignment = a;
            body = body[..comma];
        }

        var name = body.TrimStart('@', '$').Trim();
        return new Hole(raw, name, format, alignment);
    }

    private static string Format(object? value, string? format, int? alignment)
    {
        var text = value switch
        {
            null => "(null)",
            IFormattable f => f.ToString(format, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

        if (alignment is { } width)
            text = width < 0 ? text.PadRight(-width) : text.PadLeft(width);
        return text;
    }
}
