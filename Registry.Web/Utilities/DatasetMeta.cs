using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Registry.Web.Utilities;

/// <summary>The dataset metadata (<c>properties.meta</c>) that dataset-level responses carry.</summary>
public static class DatasetMeta
{
    private const string MetaProperty = "meta";

    /// <summary>
    /// The root metadata list of the saved map views. They are read through the meta API (<c>meta/get/views</c>)
    /// when a dataset page needs them, not with every dataset info and listing response, where they would grow
    /// each response by the size of the views.
    /// </summary>
    public const string ViewsKey = "views";

    /// <summary>
    /// The root metadata list of the report definitions, read through the meta API like the views and kept out of
    /// the dataset responses for the same reason.
    /// </summary>
    public const string ReportsKey = "reports";

    /// <summary>The meta keys that dataset-level responses leave out: lists the Hub reads through the meta API.</summary>
    private static readonly string[] HiddenKeys = [ViewsKey, ReportsKey];

    /// <summary>
    /// <paramref name="properties"/> without <see cref="HiddenKeys"/> in its <c>meta</c> (the saved map views and the
    /// report definitions); every other property and meta key is kept. Nothing given is changed (the properties of a
    /// summary are cached and shared): when one of the keys is there it returns a new dictionary with a new
    /// <c>meta</c> element, otherwise the same dictionary.
    /// </summary>
    public static Dictionary<string, JsonElement>? WithoutHiddenKeys(Dictionary<string, JsonElement>? properties)
    {
        if (properties == null ||
            !properties.TryGetValue(MetaProperty, out var meta) ||
            meta.ValueKind != JsonValueKind.Object ||
            !HiddenKeys.Any(key => meta.TryGetProperty(key, out _)))
            return properties;

        var node = JsonNode.Parse(meta.GetRawText())!.AsObject();
        foreach (var key in HiddenKeys)
            node.Remove(key);

        return new Dictionary<string, JsonElement>(properties) { [MetaProperty] = node.ToElement() };
    }
}
