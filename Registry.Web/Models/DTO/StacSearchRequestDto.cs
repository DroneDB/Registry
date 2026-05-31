using System.Collections.Generic;
using Newtonsoft.Json;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Request body / query parameters for the STAC API Item Search endpoint
/// (https://api.stacspec.org/v1.0.0/item-search).
/// </summary>
public class StacSearchRequestDto
{
    /// <summary>
    /// Bounding box filter [minX, minY, maxX, maxY] in WGS84.
    /// </summary>
    [JsonProperty("bbox")]
    public double[] Bbox { get; set; }

    /// <summary>
    /// GeoJSON geometry filter. Mutually exclusive with <see cref="Bbox"/>.
    /// </summary>
    [JsonProperty("intersects")]
    public object Intersects { get; set; }

    /// <summary>
    /// Single RFC 3339 datetime or an interval "start/end" (".." for open ends).
    /// </summary>
    [JsonProperty("datetime")]
    public string Datetime { get; set; }

    /// <summary>
    /// Limit of items returned per page.
    /// </summary>
    [JsonProperty("limit")]
    public int? Limit { get; set; }

    /// <summary>
    /// Restrict the search to specific collection ids.
    /// </summary>
    [JsonProperty("collections")]
    public string[] Collections { get; set; }

    /// <summary>
    /// Restrict the search to specific item ids.
    /// </summary>
    [JsonProperty("ids")]
    public string[] Ids { get; set; }

    /// <summary>
    /// Opaque pagination token returned in the "next" link of a previous response.
    /// </summary>
    [JsonProperty("token")]
    public string Token { get; set; }
}
