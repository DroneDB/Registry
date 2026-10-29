using System.Text.Json;
using System.Text.Json.Nodes;

namespace Registry.Web.Utilities;

/// <summary>Conversions of the JSON DOM that task parameters are built with.</summary>
public static class JsonNodeExtensions
{
    /// <summary>The node as a <see cref="JsonElement"/> that does not depend on a disposed document.</summary>
    public static JsonElement ToElement(this JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}
