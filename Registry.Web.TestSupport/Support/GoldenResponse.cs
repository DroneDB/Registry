using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Registry.Web.Test.Support;

/// <summary>
/// The golden form of one HTTP response (the golden JSON contract tests): a pretty-printed JSON
/// envelope
/// <code>
/// { "request": "GET /sys/features", "caller": "anonymous", "status": 200,
///   "contentType": "application/json; charset=utf-8", "bodyKind": "json", "body": { … } }
/// </code>
/// The body is the parsed response JSON, scrubbed by a <see cref="JsonScrubber"/>, with its keys in
/// the order the server wrote them and its numbers as written (<c>1.0</c> stays <c>1.0</c>). A
/// body that is not JSON is kept as a (scrubbed) string, an empty body is <c>null</c>;
/// <c>bodyKind</c> (<c>json</c>, <c>text</c>, <c>empty</c>) tells them apart, so a plain-text
/// <c>1.2.3</c> and a JSON <c>"1.2.3"</c> give different goldens. Response headers that are part of
/// the contract (e.g. <c>X-Total-Count</c>) are listed under <c>headers</c> when given.
/// String escaping is normalised (non-ASCII and HTML characters are written as is), so only the
/// JSON value is compared, not how the serializer escaped it.
/// Compare the result with <see cref="SnapshotFile.VerifyDocument"/>.
/// </summary>
public static class GoldenResponse
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <param name="request">"METHOD /path" as sent (route values in clear, no host).</param>
    /// <param name="caller">who sent it (e.g. <c>anonymous</c>, <c>user</c>, <c>admin</c>).</param>
    /// <param name="status">the HTTP status code.</param>
    /// <param name="contentType">the response Content-Type header, null when there is none.</param>
    /// <param name="body">the response body text.</param>
    /// <param name="scrubber">replaces the volatile values of the body.</param>
    /// <param name="normalise">optional, runs on the scrubbed body (e.g. <see cref="JsonScrubber.SortArray"/>).</param>
    /// <param name="headers">optional contract headers (name -&gt; value), written in name order.</param>
    public static string Render(string request, string caller, int status, string? contentType, string body,
        JsonScrubber scrubber, Action<JsonNode?>? normalise = null,
        IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        var (kind, parsed) = ParseBody(body, scrubber);
        normalise?.Invoke(parsed);

        var envelope = new JsonObject
        {
            ["request"] = request,
            ["caller"] = caller,
            ["status"] = status,
            ["contentType"] = contentType
        };

        var recorded = headers?.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        if (recorded.Count > 0)
        {
            var headerObject = new JsonObject();
            foreach (var (name, value) in recorded)
                headerObject[name] = scrubber.ScrubText(value);
            envelope["headers"] = headerObject;
        }

        envelope["bodyKind"] = kind;
        envelope["body"] = parsed;

        return envelope.ToJsonString(WriteOptions) + "\n";
    }

    private static (string Kind, JsonNode? Body) ParseBody(string body, JsonScrubber scrubber)
    {
        if (body.Length == 0)
            return ("empty", null);

        try
        {
            return ("json", scrubber.Scrub(JsonNode.Parse(body)));
        }
        catch (JsonException)
        {
            return ("text", JsonValue.Create(scrubber.ScrubText(body)));
        }
    }
}
