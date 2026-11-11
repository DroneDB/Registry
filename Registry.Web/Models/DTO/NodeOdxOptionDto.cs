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
    // Help text with its choices and default written out (the node's %(choices)s / %(default)s are replaced).
    string? Help,
    // The node's default value (string, number, or bool): the member is named "value", not "default".
    object? Value);
