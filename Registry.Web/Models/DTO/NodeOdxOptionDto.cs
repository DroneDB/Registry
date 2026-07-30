#nullable enable
namespace Registry.Web.Models.DTO;

/// <summary>
/// A single processing option from a NodeODX node, returned by
/// <c>GET /sys/processingNodes/{nodeId}/options</c>.
/// </summary>
public sealed record NodeOdxOptionDto(
    // Option name (kebab-case), e.g. "dem-resolution"
    string Name,
    // Option type: "bool", "enum", "string", "int", "float", "json".
    string Type,
    // Domain: string (unit label) for scalars or string[] for enum choices.
    object? Domain,
    // Help text, may contain <c>{choices}</c> and <c>{default}</c> placeholders.
    string? Help,
    // Default value (string, number, or bool).
    object? Value);
