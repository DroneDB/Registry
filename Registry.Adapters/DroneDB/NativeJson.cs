#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// Deserializes the null-terminated UTF-8 JSON that libddb returns straight from native memory.
/// Unlike <c>Marshal.PtrToStringUTF8</c> + <c>JsonConvert.DeserializeObject</c>, it never builds
/// the UTF-16 copy of the whole payload (35 MB for a 50k-entry listing, on the large object heap).
/// </summary>
public static class NativeJson
{
    private const int ReaderBufferSize = 64 * 1024;

    /// <summary>
    /// The value encoded in the JSON at <paramref name="utf8"/>, or default for a null pointer,
    /// an empty string or a JSON null. Does not free the buffer.
    /// </summary>
    public static unsafe T? Deserialize<T>(IntPtr utf8)
    {
        if (utf8 == IntPtr.Zero)
            return default;

        var bytes = (byte*)utf8;
        var length = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(bytes).Length;
        if (length == 0)
            return default;

        using var stream = new UnmanagedMemoryStream(bytes, length);
        using var reader = new StreamReader(stream, Encoding.UTF8, false, ReaderBufferSize);
        using var json = new JsonTextReader(reader);
        // Same settings as JsonConvert.DeserializeObject (honours JsonConvert.DefaultSettings)
        return JsonSerializer.CreateDefault().Deserialize<T>(json);
    }
}
