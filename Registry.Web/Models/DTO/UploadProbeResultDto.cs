#nullable disable
namespace Registry.Web.Models.DTO;

/// <summary>
/// Result of the upload self-test.
/// </summary>
/// <param name="Bytes">Bytes received.</param>
/// <param name="ServerMs">Milliseconds between the start of the action and the last body byte.</param>
public record UploadProbeResultDto(long Bytes, double ServerMs);
