using System;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Ports;

/// <summary>A file part of an upload request, already on disk.</summary>
/// <param name="Path">Destination path in the dataset.</param>
/// <param name="File">The streamed temp file.</param>
/// <param name="Overwrite">Whether the part may replace a file that holds its name (D-040); the internal callers
/// replace, a client upload says so with its <c>overwrite</c> field.</param>
/// <param name="Refusal">Why the part was refused before its first byte was stored (nothing of it is on disk), or null.</param>
public sealed record StreamedPart(string Path, StreamedFile File, bool Overwrite = true, Exception? Refusal = null);
