using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Registry.Ports.Json;

/// <summary>
/// How Registry writes and reads JSON: the one place that names a serializer setting. Controllers, the
/// RFC 9457 error writers, the caches, the stores and the readers of libddb's output take their options from here,
/// so the wire cannot drift between them (<c>ApiJson</c> in Registry.Web for the HTTP and storage wire, <c>NativeJson</c>
/// in Registry.Adapters for libddb's). The settings reproduce what Json.NET did for the Hub, the ddb CLI and
/// scripts (see the converters of this namespace for the behaviours that are not options).
/// </summary>
public static class RegistryJson
{
    /// <summary>
    /// A new options instance with the settings applied; <paramref name="resolvers"/> (the source-generated contexts,
    /// first match wins) are consulted before reflection, which stays last for the framework's own types
    /// (<c>ProblemDetails</c>) and for collections of generated types.
    /// </summary>
    public static JsonSerializerOptions CreateOptions(params IJsonTypeInfoResolver[] resolvers)
    {
        var options = new JsonSerializerOptions();
        Configure(options, resolvers);
        return options;
    }

    /// <summary>
    /// Applies the settings to <paramref name="options"/> (MVC's, the HTTP ones, a new instance).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>camelCase member names, dictionary keys untouched, explicit names (<c>point_geom</c>) kept; read case-insensitively;</item>
    /// <item>nulls written (Json.NET's default); a member that was <c>NullValueHandling.Ignore</c> says so with an attribute;</item>
    /// <item>text written as is (no <c>é</c>, no HTML escaping), as Json.NET did: the body is JSON for an API, never
    /// embedded in HTML;</item>
    /// <item>numbers read from strings, <c>NaN</c> and infinity as strings; comments and trailing commas skipped;</item>
    /// <item>unknown members ignored: the Hub, the ddb CLI and scripts send members the DTOs do not read.</item>
    /// </list>
    /// </remarks>
    public static void Configure(JsonSerializerOptions options, params IJsonTypeInfoResolver[] resolvers)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (resolvers.Length > 0)
            options.TypeInfoResolver = JsonTypeInfoResolver.Combine([.. resolvers, new DefaultJsonTypeInfoResolver()]);

        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = null;
        options.PropertyNameCaseInsensitive = true;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals;
        options.ReadCommentHandling = JsonCommentHandling.Skip;
        options.AllowTrailingCommas = true;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip;
        options.MaxDepth = 64;

        AddIfMissing(options, new UtcDateTimeConverter());
        AddIfMissing(options, new LegacyDoubleConverter());
        AddIfMissing(options, new LegacySingleConverter());
        AddIfMissing(options, new LenientBooleanConverter());
        AddIfMissing(options, new LenientStringConverter());
        AddIfMissing(options, new LenientEnumConverterFactory());
    }

    private static void AddIfMissing(JsonSerializerOptions options, JsonConverter converter)
    {
        foreach (var existing in options.Converters)
            if (existing.GetType() == converter.GetType())
                return;
        options.Converters.Add(converter);
    }
}
