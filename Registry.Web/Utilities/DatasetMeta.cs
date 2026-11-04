using System.Collections.Generic;
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
    /// <paramref name="properties"/> without <see cref="ViewsKey"/> in its <c>meta</c>; every other property and
    /// meta key is kept. Nothing given is changed (the properties of a summary are cached and shared): when the
    /// views are there it returns a new dictionary with a new <c>meta</c> element, otherwise the same dictionary.
    /// </summary>
    public static Dictionary<string, JsonElement>? WithoutViews(Dictionary<string, JsonElement>? properties)
    {
        if (properties == null ||
            !properties.TryGetValue(MetaProperty, out var meta) ||
            meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty(ViewsKey, out _))
            return properties;

        var node = JsonNode.Parse(meta.GetRawText())!.AsObject();
        node.Remove(ViewsKey);

        return new Dictionary<string, JsonElement>(properties) { [MetaProperty] = node.ToElement() };
    }
}
