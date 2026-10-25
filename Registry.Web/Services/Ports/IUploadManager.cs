#nullable enable
using Registry.Web.Utilities;

namespace Registry.Web.Services.Ports;

/// <summary>A file part of an upload request, already on disk.</summary>
/// <param name="Path">Destination path in the dataset.</param>
/// <param name="File">The streamed temp file.</param>
public sealed record StreamedPart(string Path, StreamedFile File);
