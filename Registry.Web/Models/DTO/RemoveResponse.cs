using Newtonsoft.Json;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Response containing the count of removed items.
/// </summary>
public class RemoveResponse
{
    /// <summary>
    /// The number of items that were removed.
    /// </summary>
    [JsonProperty("removed")]
    public int Removed { get; set; }

    public RemoveResponse()
    {
    }

    public RemoveResponse(int removed)
    {
        Removed = removed;
    }
}
