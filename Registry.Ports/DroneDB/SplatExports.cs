#nullable enable
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

/// <summary>
/// What limits the training data of a splat (<c>DDBExportSplatTrainingData</c>); a member left null keeps libddb's default.
/// </summary>
/// <param name="MaxImageSize">The longest side in pixels of the exported images (0 exports the originals).</param>
/// <param name="MaxPoints">The most points of the exported sparse model, thinned on a 3D grid (0 writes none).</param>
public sealed record SplatTrainingOptions(int? MaxImageSize = null, int? MaxPoints = null);

/// <summary>The result of <c>DDBExportSplatTrainingData</c>: what the user should know and what the model holds.</summary>
public sealed class SplatTrainingExport
{
    /// <summary>What to know before training: images left out, an approximated lens, no point cloud, a nadir-only flight.</summary>
    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; set; } = [];

    /// <summary>The images of the exported model.</summary>
    [JsonPropertyName("images")]
    public int Images { get; set; }

    /// <summary>The points of the exported model.</summary>
    [JsonPropertyName("points")]
    public int Points { get; set; }
}

/// <summary>The result of <c>DDBExportSplatSpz</c>: the <c>.spz</c> file made for other tools.</summary>
public sealed class SplatSpzExport
{
    /// <summary>The absolute path of the file, in the build folder of the entry.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>The artifact role of the file (<see cref="ArtifactRoles.GsplatExport"/>).</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    /// <summary>The file was already there (an earlier export, still newer than the dataset file).</summary>
    [JsonPropertyName("cached")]
    public bool Cached { get; set; }
}
