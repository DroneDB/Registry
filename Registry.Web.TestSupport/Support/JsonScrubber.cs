#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Registry.Web.Test.Support;

/// <summary>
/// Replaces the volatile values of a JSON document (tokens, GUIDs, timestamps, versions, temp
/// paths) with typed placeholders, so a golden body stays stable across runs and machines while
/// keeping its shape: object keys keep their order and casing, a scrubbed string stays a string
/// and a scrubbed number stays a number (<c>0</c>). Rules, applied in this order:
/// <list type="number">
/// <item>by property name (case-insensitive, so a casing change of the wire format is still
/// scrubbed and shows in the golden as a key change): the whole value becomes the placeholder,
/// whatever its type (e.g. <c>"token"</c> -&gt; <c>"&lt;token&gt;"</c>, a version that can be
/// null -&gt; <c>"&lt;version&gt;"</c>);</item>
/// <item>every string: the given substrings (e.g. the factory's temp root -&gt;
/// <c>&lt;root&gt;</c>), then a whole value equal to a given literal (e.g. the Registry version),
/// then a whole JWT -&gt; <c>&lt;jwt&gt;</c>, a whole ISO 8601 date-time -&gt; a placeholder that
/// keeps its offset form (<c>&lt;datetime&gt;</c> without offset, <c>&lt;datetime:Z&gt;</c> for UTC
/// <c>Z</c>, <c>&lt;datetime:offset&gt;</c> for <c>±hh:mm</c>: how a serializer writes the
/// <see cref="DateTimeKind"/> is part of the wire format; the fraction digits are not, Json.NET
/// trims trailing zeros), and every GUID inside the string -&gt; <c>&lt;guid&gt;</c>.</item>
/// </list>
/// Arrays keep the server order: sort them in the test only where the order is not part of the
/// contract (<see cref="SortArray"/>).
/// </summary>
public sealed partial class JsonScrubber
{
    public const string TokenPlaceholder = "<token>";
    public const string JwtPlaceholder = "<jwt>";
    public const string GuidPlaceholder = "<guid>";
    public const string DateTimePlaceholder = "<datetime>";
    public const string UtcDateTimePlaceholder = "<datetime:Z>";
    public const string OffsetDateTimePlaceholder = "<datetime:offset>";
    public const string VersionPlaceholder = "<version>";
    public const string RootPlaceholder = "<root>";

    private readonly Dictionary<string, JsonNode> _properties;
    private readonly List<KeyValuePair<string, string>> _substrings;
    private readonly Dictionary<string, string> _literals;

    /// <param name="properties">property name (case-insensitive) -&gt; placeholder value (string or number).</param>
    /// <param name="substrings">text found inside any string -&gt; replacement (longest first).</param>
    /// <param name="literals">whole string value -&gt; replacement.</param>
    public JsonScrubber(IReadOnlyDictionary<string, JsonNode>? properties = null,
        IReadOnlyDictionary<string, string>? substrings = null,
        IReadOnlyDictionary<string, string>? literals = null)
    {
        _properties = new Dictionary<string, JsonNode>(properties ?? new Dictionary<string, JsonNode>(),
            StringComparer.OrdinalIgnoreCase);
        _substrings = (substrings ?? new Dictionary<string, string>())
            .Where(s => s.Key.Length > 0)
            .OrderByDescending(s => s.Key.Length)
            .ToList();
        _literals = new Dictionary<string, string>(literals ?? new Dictionary<string, string>(), StringComparer.Ordinal);
    }

    /// <summary>Scrubs <paramref name="node"/> in place and returns it.</summary>
    public JsonNode? Scrub(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    var current = obj[name];
                    var scrubbed = _properties.TryGetValue(name, out var placeholder)
                        ? placeholder.DeepClone()
                        : Scrub(current);
                    // Containers are scrubbed in place; only a replaced value is re-attached
                    if (!ReferenceEquals(scrubbed, current))
                        obj[name] = scrubbed;
                }

                return obj;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var current = array[i];
                    var scrubbed = Scrub(current);
                    if (!ReferenceEquals(scrubbed, current))
                        array[i] = scrubbed;
                }

                return array;

            case JsonValue value when value.TryGetValue<string>(out var text):
                var scrubbedText = ScrubText(text);
                return string.Equals(scrubbedText, text, StringComparison.Ordinal) ? value : JsonValue.Create(scrubbedText);

            default:
                return node;
        }
    }

    /// <summary>The string rules of <see cref="Scrub"/> for one text (also used for non-JSON bodies).</summary>
    public string ScrubText(string text)
    {
        foreach (var (find, replacement) in _substrings)
            text = text.Replace(find, replacement, StringComparison.Ordinal);

        if (_literals.TryGetValue(text, out var literal))
            return literal;
        if (JwtPattern().IsMatch(text))
            return JwtPlaceholder;
        var dateTime = DateTimePattern().Match(text);
        if (dateTime.Success)
            return dateTime.Groups["offset"].Value switch
            {
                "" => DateTimePlaceholder,
                "Z" => UtcDateTimePlaceholder,
                _ => OffsetDateTimePlaceholder
            };

        return GuidPattern().Replace(text, GuidPlaceholder);
    }

    /// <summary>
    /// Sorts the array at <paramref name="path"/> (property names from the root, empty for a root
    /// array) by the string value of <paramref name="key"/> of each element (ordinal), for arrays
    /// whose server order is not deterministic. A missing array is left alone.
    /// </summary>
    public static void SortArray(JsonNode? root, IReadOnlyList<string> path, string key)
    {
        var node = path.Aggregate(root, (current, name) => current?[name]);
        if (node is not JsonArray array)
            return;

        var sorted = array
            .Select(item => item?.DeepClone())
            .OrderBy(item => item?[key]?.ToJsonString(), StringComparer.Ordinal)
            .ToList();
        array.Clear();
        foreach (var item in sorted)
            array.Add(item);
    }

    [GeneratedRegex(@"^eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*$")]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(?<offset>Z|[+-]\d{2}:?\d{2})?$")]
    private static partial Regex DateTimePattern();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();
}
