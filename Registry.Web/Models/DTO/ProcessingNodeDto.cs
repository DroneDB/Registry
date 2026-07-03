namespace Registry.Web.Models.DTO;

/// <summary>
/// Descriptor for a configured processing node, returned by the
/// <c>GET /sys/processingNodes</c> endpoint. Only non-sensitive fields
/// (id and title) are exposed; URL and token are never included.
/// </summary>
/// <param name="Id">Stable identifier used to target this node from a submit request.</param>
/// <param name="Title">Human-readable title displayed in the UI.</param>
public sealed record ProcessingNodeDto(string Id, string Title);
