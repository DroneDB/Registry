#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Registry.Ports;
using Registry.Ports.Json;
using Registry.Web.Models.DTO.Ogc;

namespace Registry.Web.Services.Managers.Wfs;

/// <summary>A field of a vector layer as DroneDB describes it (the document is gone once it is read).</summary>
internal sealed record LayerField(string? Name, string? Type);

/// <summary>The fields of a vector layer (out of <c>WfsManager</c>): what the schema and the local filter both need.</summary>
internal static class WfsLayerFields
{
    /// <summary>The fields of the layer from DroneDB's description, or null when it lists none.</summary>
    public static IReadOnlyList<LayerField>? Read(IDdbWrapper ddb, string gpkg, OgcLayerDto layer)
    {
        using var describe = JsonDocument.Parse(ddb.DescribeVector(gpkg, layer.InnerLayerName));
        return describe.RootElement.Member("layers") is { ValueKind: JsonValueKind.Array } layers &&
               layers.GetArrayLength() > 0 &&
               layers[0].Member("fields") is { ValueKind: JsonValueKind.Array } fields
            ? [.. fields.Items().Where(f => f.ValueKind == JsonValueKind.Object)
                .Select(f => new LayerField(f.Member("name").Text(), f.Member("type").Text()))]
            : null;
    }
}
