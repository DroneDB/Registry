#nullable enable
using System.Collections.Generic;
using System.Text.Json;

namespace Registry.Web.Services.HeavyTasks.Ports;

/// <summary>
/// The parameters of a heavy-tool request (<see cref="Models.HeavyToolRequest.Params"/>), read
/// leniently, the one way every tool reads them: a missing property, a value of another JSON kind
/// or a request whose parameters are not an object reads as absent (null), never as an error.
/// Property names are case-sensitive. Task requests stored in the job queue keep the meaning they
/// were stored with (D4).
/// </summary>
public readonly struct ToolParameters(JsonElement json)
{
    /// <summary>The string <paramref name="name"/>, or null.</summary>
    public string? Text(string name) =>
        Get(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    /// <summary>The boolean <paramref name="name"/> (JSON true/false only), or null.</summary>
    public bool? Flag(string name) =>
        Get(name, out var el)
            ? el.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;

    /// <summary>The integer <paramref name="name"/> (a JSON number that fits a long), or null.</summary>
    public long? WholeNumber(string name) =>
        Get(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v) ? v : null;

    /// <summary>
    /// The strings of the array <paramref name="name"/> (other items skipped), or null when it is not
    /// an array.
    /// </summary>
    public List<string>? Strings(string name)
    {
        if (!Get(name, out var el) || el.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
                list.Add(item.GetString()!);
        return list;
    }

    /// <summary>
    /// The non-blank strings of the array <paramref name="name"/>, or null when it is not an array or
    /// holds none.
    /// </summary>
    public string[]? NonBlankStrings(string name)
    {
        var all = Strings(name);
        if (all == null) return null;
        var list = all.FindAll(s => !string.IsNullOrWhiteSpace(s));
        return list.Count > 0 ? [.. list] : null;
    }

    /// <summary>The array <paramref name="name"/> as raw JSON text, or null when it is not an array.</summary>
    public string? RawArray(string name) =>
        Get(name, out var el) && el.ValueKind == JsonValueKind.Array ? el.GetRawText() : null;

    /// <summary>The object <paramref name="name"/>, or null when it is not an object.</summary>
    public JsonElement? JsonObject(string name) =>
        Get(name, out var el) && el.ValueKind == JsonValueKind.Object ? el : null;

    private bool Get(string name, out JsonElement value)
    {
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out value))
            return true;
        value = default;
        return false;
    }
}
