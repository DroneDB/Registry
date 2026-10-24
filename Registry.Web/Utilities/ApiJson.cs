#nullable enable
using System;
using System.Text.Json;
using Registry.Ports.Json;

namespace Registry.Web.Utilities;

/// <summary>
/// The JSON options of the Registry API and of everything Registry stores as JSON (RQ-14, D3): MVC's formatters, the
/// RFC 9457 writers, the caches, the chunk manifests and the push sessions all use these, so a type is written the same
/// way everywhere. <see cref="RegistryJson"/> sets the behaviour; the generated contracts of the API
/// (<see cref="RegistryJsonContext"/>) and of libddb (<see cref="DdbJsonContext"/>) are consulted before reflection.
/// </summary>
public static class ApiJson
{
    /// <summary>The shared options. Read-only: copy them to add a converter.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>A new, writable copy of the API options.</summary>
    public static JsonSerializerOptions Create() => RegistryJson.CreateOptions(RegistryJsonContext.Default, DdbJsonContext.Default);

    /// <summary>
    /// Applies the API settings to the options of a framework component (MVC's formatters, the problem details
    /// writer): <see cref="RegistryJson"/>'s behaviour, the generated contracts, and the dataset path rule for the
    /// bodies that carry a <see cref="DatasetPath"/>.
    /// </summary>
    public static void Configure(JsonSerializerOptions options, DatasetPathPolicy paths)
    {
        RegistryJson.Configure(options, RegistryJsonContext.Default, DdbJsonContext.Default);
        options.Converters.Add(new DatasetPathJsonConverter(paths));
    }

    /// <summary><typeparamref name="T"/> from JSON text.</summary>
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary><paramref name="value"/> as JSON text.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
