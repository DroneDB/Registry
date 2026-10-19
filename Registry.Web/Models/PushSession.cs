#nullable enable
using Newtonsoft.Json;
using Registry.Ports.DroneDB;

namespace Registry.Web.Models;

/// <summary>
/// State of a push between Init and Commit, kept in the push folder.
/// </summary>
public class PushSession
{
    /// <summary>
    /// Checksum of the dataset stamp when the push started: Commit applies <see cref="Delta"/>
    /// only if the dataset has not changed since.
    /// </summary>
    [JsonProperty("ourChecksum")]
    public required string OurChecksum { get; init; }

    /// <summary>Delta from the dataset to the client's stamp, computed once by Init.</summary>
    [JsonProperty("delta")]
    public required Delta Delta { get; init; }
}
