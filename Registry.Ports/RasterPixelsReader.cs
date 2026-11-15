using System;

namespace Registry.Ports;

/// <summary>
/// Reads the pixels of a render (<see cref="IDdbWrapper.RenderRasterRegionPixels"/>): RGBA, 8 bits a channel, rows top
/// to bottom, straight alpha. They are valid while the reader runs only.
/// </summary>
public delegate void RasterPixelsReader(ReadOnlySpan<byte> rgba);
