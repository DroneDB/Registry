using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text.Json;
using Registry.Ports.DroneDB;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// The one call / error / free / deserialize envelope of every libddb call,
/// generalized from the batch add. Each helper runs the native call, and on failure reads the last
/// error and reason synchronously, on the same thread, before anything else (no await, no other
/// libddb call in between), then throws the mapped exception (<see cref="DdbResultMapper"/>). An
/// output is read only on success and freed in a finally block whenever it is not null. Each call
/// runs under the current <see cref="DdbOperation"/> (<see cref="NativeOperation"/>) and, for an
/// export that honours cancel tokens, the current <see cref="DdbCancellation"/> (<see cref="NativeCancellation"/>): a canceled call throws <see cref="DdbCanceledException"/> with the managed token.
/// </summary>
internal static class NativeCall
{
    internal delegate DdbResult OutputCall(out IntPtr output);

    internal delegate DdbResult BufferCall(out IntPtr buffer, out int size);

    /// <summary>Reads a value out of a native UTF-8 JSON output, in place.</summary>
    internal delegate T Utf8Reader<out T>(ReadOnlySpan<byte> utf8);

    /// <summary>
    /// Throws the mapped exception of a failed call. Must run right after the call, on its thread.
    /// <paramref name="context"/> (the call's subject, e.g. its file) is appended to the message.
    /// </summary>
    internal static void Check(DdbResult result, string operation, string? context = null) =>
        Check(result, operation, context, CancellationToken.None);

    private static void Check(DdbResult result, string operation, string? context, CancellationToken token)
    {
        if (result == DdbResult.Success)
            return;

        // Read both before anything else: the next libddb call on this thread clears them
        var message = DdbResultMapper.GetNativeLastError();
        var reason = DdbResultMapper.GetNativeLastErrorReason();
        if (context != null)
            message = $"{(message.Length == 0 ? $"{operation} failed ({result})" : message)} ({context})";

        throw DdbResultMapper.Map(result, operation, message, reason, token);
    }

    /// <summary>
    /// Prepares the thread for the next native call of <paramref name="operation"/> (the export's name): its
    /// cancel token if it gets one, then the operation id, then the token is set. The order is libddb's:
    /// creating a token is an operation call, which resets the operation id and the token of the thread when
    /// it returns; setting them are settings, which keep each other. Dispose the result after the call (and
    /// after <see cref="Check(DdbResult, string, string?, CancellationToken)"/>).
    /// </summary>
    private static NativeCancellation Begin(string operation)
    {
        var cancellation = NativeCancellation.Create(operation);
        NativeOperation.Bind();
        cancellation.Arm();
        return cancellation;
    }

    /// <summary>A call without output.</summary>
    internal static void Invoke(string operation, Func<DdbResult> call, string? context = null)
    {
        using var cancellation = Begin(operation);
        Check(call(), operation, context, cancellation.Token);
    }

    /// <summary>A call whose output is JSON, deserialized in place (<see cref="NativeJson"/>); a missing or null output fails.</summary>
    internal static T Invoke<T>(string operation, OutputCall call) where T : class
    {
        var output = IntPtr.Zero;
        try
        {
            using var cancellation = Begin(operation);
            Check(call(out output), operation, null, cancellation.Token);
            return Deserialize<T>(operation, output) ?? throw new DdbException($"{operation} returned no output");
        }
        finally
        {
            FreeString(output);
        }
    }

    /// <summary>
    /// A call whose output is a JSON document of any shape (STAC data, a meta count): copied out of the native buffer
    /// as a <see cref="JsonElement"/>; a missing output fails.
    /// </summary>
    internal static JsonElement InvokeDocument(string operation, OutputCall call)
    {
        var output = IntPtr.Zero;
        try
        {
            using var cancellation = Begin(operation);
            Check(call(out output), operation, null, cancellation.Token);
            var document = Deserialize<JsonElement>(operation, output);
            return document.ValueKind == JsonValueKind.Undefined
                ? throw new DdbException($"{operation} returned no output")
                : document;
        }
        finally
        {
            FreeString(output);
        }
    }

    /// <summary>
    /// A call whose JSON output may be absent: null when libddb answers <see cref="DdbResult.NotFound"/>
    /// (its normal "no such entry" answer), the deserialized output otherwise.
    /// </summary>
    internal static T? InvokeOrNotFound<T>(string operation, OutputCall call) where T : class
    {
        var output = IntPtr.Zero;
        try
        {
            using var cancellation = Begin(operation);
            var result = call(out output);
            if (result == DdbResult.NotFound)
                return null;

            Check(result, operation, null, cancellation.Token);
            return Deserialize<T>(operation, output) ?? throw new DdbException($"{operation} returned no output");
        }
        finally
        {
            FreeString(output);
        }
    }

    /// <summary>A call whose string output may be absent: null when libddb answers <see cref="DdbResult.NotFound"/>.</summary>
    internal static string? InvokeStringOrNotFound(string operation, OutputCall call)
    {
        var output = IntPtr.Zero;
        try
        {
            using var cancellation = Begin(operation);
            var result = call(out output);
            if (result == DdbResult.NotFound)
                return null;

            Check(result, operation, null, cancellation.Token);
            return Marshal.PtrToStringUTF8(output) ?? throw new DdbException($"{operation} returned no output");
        }
        finally
        {
            FreeString(output);
        }
    }

    /// <summary>A call whose output is a string; null when libddb returned none.</summary>
    internal static string? InvokeString(string operation, OutputCall call)
    {
        var output = IntPtr.Zero;
        try
        {
            using var cancellation = Begin(operation);
            Check(call(out output), operation, null, cancellation.Token);
            return Marshal.PtrToStringUTF8(output);
        }
        finally
        {
            FreeString(output);
        }
    }

    /// <summary>A call whose output is a string that must not be empty.</summary>
    internal static string InvokeRequiredString(string operation, OutputCall call)
    {
        var value = InvokeString(operation, call);
        return string.IsNullOrWhiteSpace(value) ? throw new DdbException($"{operation} returned no output") : value;
    }

    /// <summary>A call whose output is a VSI buffer (images, tiles): copied, then freed with DDBVSIFree.</summary>
    internal static byte[] InvokeBuffer(string operation, BufferCall call, string? context = null)
    {
        var buffer = IntPtr.Zero;
        try
        {
            using var cancellation = Begin(operation);
            Check(call(out buffer, out var size), operation, context, cancellation.Token);
            if (buffer == IntPtr.Zero || size < 0)
                throw new DdbException($"{operation} returned no buffer");

            var bytes = new byte[size];
            Marshal.Copy(buffer, bytes, 0, size);
            return bytes;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                NativeDdb.DDBVSIFree(buffer);
        }
    }

    /// <summary>
    /// A call whose small JSON output is read in place with <paramref name="read"/>
    /// (<c>System.Text.Json.Utf8JsonReader</c>): no Json.NET serializer and no UTF-16 copy per call,
    /// for exports called once per item (the path rule, once per archive entry or walked file).
    /// </summary>
    internal static unsafe T InvokeUtf8<T>(string operation, OutputCall call, Utf8Reader<T> read)
    {
        var output = IntPtr.Zero;
        try
        {
            using (var cancellation = Begin(operation))
                Check(call(out output), operation, null, cancellation.Token);
            if (output == IntPtr.Zero)
                throw new DdbException($"{operation} returned no output");

            try
            {
                return read(MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)output));
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new DdbException($"{operation} returned JSON that is not a {typeof(T).Name}: {ex.Message}", ex);
            }
        }
        finally
        {
            FreeString(output);
        }
    }

    /// <summary>JSON at <paramref name="output"/> (not freed), with a parse failure reported as a <see cref="DdbException"/>.</summary>
    internal static T? Deserialize<T>(string operation, IntPtr output)
    {
        try
        {
            return NativeJson.Deserialize<T>(output);
        }
        catch (JsonException ex)
        {
            throw new DdbException($"{operation} returned JSON that is not a {typeof(T).Name}: {ex.Message}", ex);
        }
    }

    internal static void FreeString(IntPtr output)
    {
        if (output != IntPtr.Zero)
            NativeDdb.DDBFree(output);
    }
}
