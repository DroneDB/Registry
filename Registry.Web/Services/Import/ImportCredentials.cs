#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Registry.Ports.Import;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Import;

/// <summary>
/// The credentials in the parameters of an import task (RQ-12, out of the import managers and tools): encrypted by
/// the web host before the task is queued (so a password is never stored in the job payload or the job index in
/// plaintext) and decrypted by the worker. An encrypted value is the cipher text behind the <see cref="Prefix"/>.
/// </summary>
public sealed class ImportCredentials
{
    /// <summary>What an encrypted value starts with.</summary>
    public const string Prefix = "ENC:";

    private readonly IImportCredentialProtector _protector;

    public ImportCredentials(IImportCredentialProtector protector) => _protector = protector;

    /// <summary><paramref name="plaintext"/> encrypted, behind the <see cref="Prefix"/>.</summary>
    public string Seal(string plaintext) => Prefix + _protector.Protect(plaintext);

    /// <summary>The plaintext of <paramref name="raw"/>: decrypted when it starts with the <see cref="Prefix"/>, as it is otherwise.</summary>
    public string Open(string raw) =>
        raw.StartsWith(Prefix, StringComparison.Ordinal) ? _protector.Unprotect(raw[Prefix.Length..]) : raw;

    /// <summary>
    /// The parameters of a source with its sensitive fields (<see cref="ImportSourceDefinitions.SensitiveFields"/>) encrypted;
    /// empty values stay empty.
    /// </summary>
    public JsonObject SealParameters(string sourceType, IReadOnlyDictionary<string, string> parameters)
    {
        var sensitive = SensitiveFieldsOf(sourceType);

        var sealedParameters = new JsonObject();
        foreach (var (name, raw) in parameters)
        {
            var value = raw ?? string.Empty;
            if (!string.IsNullOrEmpty(value) && sensitive.Contains(name))
                value = Seal(value);
            sealedParameters[name] = value;
        }

        return sealedParameters;
    }

    /// <summary>
    /// A copy of the source <paramref name="parameters"/> with the known sensitive fields decrypted in place. Only
    /// the fields listed for the source type are treated as cipher text, so an arbitrary user-supplied value
    /// cannot trigger a decryption.
    /// </summary>
    public JsonElement OpenParameters(string sourceType, JsonElement parameters)
    {
        var sensitive = SensitiveFieldsOf(sourceType);

        var opened = new JsonObject();
        foreach (var property in parameters.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var text = property.Value.GetString() ?? string.Empty;
                opened[property.Name] = sensitive.Contains(property.Name) && text.StartsWith(Prefix, StringComparison.Ordinal)
                    ? _protector.Unprotect(text[Prefix.Length..])
                    : text;
            }
            else
            {
                opened[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }

        return opened.ToElement();
    }

    private static IReadOnlySet<string> SensitiveFieldsOf(string sourceType) =>
        ImportSourceDefinitions.SensitiveFields.TryGetValue(sourceType, out var fields)
            ? fields
            : new HashSet<string>();
}
