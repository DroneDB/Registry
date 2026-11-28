using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Registry.Web.Services.OrganizationAssets;

/// <summary>
/// The class rules of the style sheets of an SVG (<c>.name { fill: #c90505 }</c>), so that the sanitiser can write what
/// they paint on the elements that have the class: the sheets themselves are not kept. Only a rule whose selector is one
/// class counts (it is what design tools write); a rule of another selector, an at-rule (<c>@media</c>, <c>@import</c>)
/// and a comment are left out. The declarations are not checked here: the sanitiser keeps only the safe painting ones.
/// </summary>
public sealed partial class SvgClassStyles
{
    // A selector that is one class: .name
    [GeneratedRegex(@"^\.(-?[_A-Za-z][_A-Za-z0-9-]*)\z")]
    private static partial Regex ClassSelector();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();

    private static readonly char[] Spaces = [' ', '\t', '\r', '\n', '\f'];

    // The rules of each class, as the order of the rule in the sheets and its declarations
    private readonly Dictionary<string, List<(int Order, List<KeyValuePair<string, string>> Declarations)>> _rules;

    private SvgClassStyles(Dictionary<string, List<(int, List<KeyValuePair<string, string>>)>> rules)
    {
        _rules = rules;
    }

    /// <summary>The class rules of <paramref name="sheets"/> (the text of the style elements, one after the other).</summary>
    public static SvgClassStyles Parse(string sheets)
    {
        var rules = new Dictionary<string, List<(int, List<KeyValuePair<string, string>>)>>(StringComparer.Ordinal);
        var css = Comment().Replace(sheets, " ");
        var order = 0;
        var at = 0;

        while (at < css.Length)
        {
            var open = css.IndexOfAny(['{', ';', '}'], at);
            if (open < 0)
                break;

            var prelude = css[at..open].Trim();
            if (css[open] != '{')
            {
                // A statement without a block (@import ...;) or a stray brace
                at = open + 1;
                continue;
            }

            var close = MatchingBrace(css, open);
            var block = css[(open + 1)..close];
            at = close + 1;

            // An at-rule, or a block in a block: not a rule of a class
            if (prelude.StartsWith('@') || block.Contains('{'))
                continue;

            var declarations = Declarations(block).ToList();
            foreach (var selector in prelude.Split(',', StringSplitOptions.TrimEntries))
            {
                var match = ClassSelector().Match(selector);
                if (!match.Success)
                    continue;

                if (!rules.TryGetValue(match.Groups[1].Value, out var list))
                    rules[match.Groups[1].Value] = list = [];
                list.Add((order, declarations));
            }

            order++;
        }

        return new SvgClassStyles(rules);
    }

    /// <summary>
    /// The declarations the rules give an element with these classes (its <c>class</c> attribute, or null), in the order
    /// of the rules: a later one of a property is the one that applies.
    /// </summary>
    public IEnumerable<KeyValuePair<string, string>> DeclarationsOf(string? classes)
    {
        if (string.IsNullOrWhiteSpace(classes) || _rules.Count == 0)
            return [];

        return classes.Split(Spaces, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .SelectMany(name => _rules.GetValueOrDefault(name) ?? [])
            .OrderBy(rule => rule.Order)
            .SelectMany(rule => rule.Declarations);
    }

    /// <summary>The declarations of a style attribute or of a rule (<c>fill: red; opacity: .5</c>), or none for null.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Declarations(string? style)
    {
        if (string.IsNullOrEmpty(style))
            yield break;

        foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = declaration.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Length > 0)
                yield return new KeyValuePair<string, string>(parts[0], parts[1]);
        }
    }

    // The index of the brace that closes the one at open, or the end of the text when it is not closed
    private static int MatchingBrace(string css, int open)
    {
        var depth = 0;
        for (var i = open; i < css.Length; i++)
        {
            if (css[i] == '{')
                depth++;
            else if (css[i] == '}' && --depth == 0)
                return i;
        }

        return css.Length;
    }
}
