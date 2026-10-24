#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Registry.Ports.Json;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// Deserializes the null-terminated UTF-8 JSON that libddb returns straight from native memory, with the source-generated
/// contract of libddb's own output (<see cref="DdbJsonContext"/>: its field names, <c>point_geom</c> and <c>stac_version</c>
/// among them, are never renamed). The parser reads the native bytes in place: no <c>StreamReader</c> and no 64 Ki-char
/// buffer per call (a tiny output used to cost 200 KB), and no UTF-16 copy of the payload (35 MB for a 50k-entry listing,
/// on the large object heap). A document (<see cref="JsonElement"/>) is copied out of the native buffer, so it outlives it.
/// </summary>
public static class NativeJson
{
    /// <summary>The options of libddb's wire: Registry's settings and the generated contract.</summary>
    public static JsonSerializerOptions Options { get; } = RegistryJson.CreateOptions(DdbJsonContext.Default);

    /// <summary><paramref name="value"/> as the JSON text libddb reads (its contract's names, nulls written).</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T)));

    /// <summary>
    /// The value encoded in the JSON at <paramref name="utf8"/>, or default for a null pointer, an empty string or a
    /// JSON null. Does not free the buffer.
    /// </summary>
    public static unsafe T? Deserialize<T>(IntPtr utf8)
    {
        if (utf8 == IntPtr.Zero)
            return default;

        var bytes = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)utf8);
        if (bytes.Length == 0)
            return default;

        return JsonSerializer.Deserialize(bytes, (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T)));
    }
}
