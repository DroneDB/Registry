using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

/// <summary>
/// The build state of an entry (ddb.h "build manifest"), in libddb's precedence order. Written and read by
/// libddb's lowercase names (<c>"pending"</c>).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<BuildState>))]
public enum BuildState
{
    /// <summary>Not built, failed or incomplete (and every entry that is not buildable).</summary>
    [JsonStringEnumMemberName("none")] None,

    /// <summary>A build waits for missing dependencies (a pending marker).</summary>
    [JsonStringEnumMemberName("pending")] Pending,

    /// <summary>A live build holds the build lock of the entry.</summary>
    [JsonStringEnumMemberName("building")] Building,

    /// <summary>The artifacts are complete.</summary>
    [JsonStringEnumMemberName("complete")] Complete
}
