using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Linq;
using System.Text.Json;

namespace Registry.Ports.DroneDB;

/// <summary>
/// What the loaded libddb supports (<c>DDBGetCapabilities</c>, ABI 2.0): its ABI and
/// version, its error codes and reasons, the exports that honour a cancel token and every exported
/// symbol. Read once at startup; it replaces probing an export by calling it and catching
/// <see cref="EntryPointNotFoundException"/>.
/// </summary>
public sealed class DdbCapabilities
{
    private DdbCapabilities(int abiMajor, int abiMinor, string version,
        IReadOnlyDictionary<string, int> errorCodes, IReadOnlySet<string> errorReasons,
        IReadOnlySet<string> cancellable, IReadOnlySet<string> symbols)
    {
        AbiMajor = abiMajor;
        AbiMinor = abiMinor;
        Version = version;
        ErrorCodes = errorCodes;
        ErrorReasons = errorReasons;
        Cancellable = cancellable;
        Symbols = symbols;
    }

    public int AbiMajor { get; }
    public int AbiMinor { get; }

    /// <summary><c>DDBGetVersion()</c>, e.g. "2.0.0 (git commit 1f171e5)".</summary>
    public string Version { get; }

    /// <summary>Every <c>DDBErr</c> code by name without the prefix ("NONE" = 0, ...).</summary>
    public IReadOnlyDictionary<string, int> ErrorCodes { get; }

    /// <summary>Every reason <c>DDBGetLastErrorReason()</c> returns.</summary>
    public IReadOnlySet<string> ErrorReasons { get; }

    /// <summary>The exports whose work checks a cancel token while it runs.</summary>
    public IReadOnlySet<string> Cancellable { get; }

    /// <summary>Every exported <c>DDB*</c> function.</summary>
    public IReadOnlySet<string> Symbols { get; }

    /// <summary>The work of <paramref name="symbol"/> stops when its cancel token is canceled.</summary>
    public bool IsCancellable(string symbol) => Cancellable.Contains(symbol);

    public override string ToString() => $"libddb {Version}, ABI {AbiMajor}.{AbiMinor}, {Symbols.Count} exports";

    /// <summary>Parses the <c>DDBGetCapabilities()</c> JSON (fields are only ever added: unknown ones are ignored).</summary>
    /// <exception cref="FormatException">The JSON lacks a field ABI 2.0 defines.</exception>
    public static DdbCapabilities Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("DDBGetCapabilities() did not return a JSON object", ex);
        }

        using (document)
        {
            var root = Required(document.RootElement, null, JsonValueKind.Object);
            var abi = Required(root, "abi", JsonValueKind.Object);
            return new DdbCapabilities(
                Required(abi, "major", JsonValueKind.Number).GetInt32(),
                Required(abi, "minor", JsonValueKind.Number).GetInt32(),
                Required(root, "version", JsonValueKind.String).GetString() ?? "",
                Required(root, "errorCodes", JsonValueKind.Object).EnumerateObject()
                    .ToFrozenDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.Ordinal),
                Names(root, "errorReasons"),
                Names(root, "cancellable"),
                Names(root, "symbols"));
        }
    }

    private static FrozenSet<string> Names(JsonElement root, string field) =>
        Required(root, field, JsonValueKind.Array).EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
            .ToFrozenSet(StringComparer.Ordinal);

    private static JsonElement Required(JsonElement parent, string? field, JsonValueKind kind)
    {
        if (field == null)
            return parent.ValueKind == kind ? parent : throw new FormatException("DDBGetCapabilities() did not return a JSON object");

        return parent.TryGetProperty(field, out var value) && value.ValueKind == kind
            ? value
            : throw new FormatException($"DDBGetCapabilities() has no valid \"{field}\"");
    }
}
