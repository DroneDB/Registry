#nullable enable
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Runs every thumbnail of the decorated generator on the <see cref="NativeLane.Heavy"/> lane of
/// the native work gate (REG-07 §5.2), whoever asks for it (views, upload pre-generation, STAC).
/// </summary>
public sealed class GatedThumbnailGenerator(IThumbnailGenerator inner, INativeWorkGate gate) : IThumbnailGenerator
{
    public Task GenerateThumbnailAsync(string filePath, int size, Stream output, CancellationToken ct = default) =>
        gate.RunAsync(NativeLane.Heavy, async () =>
        {
            await inner.GenerateThumbnailAsync(filePath, size, output, ct);
            return true;
        }, ct);
}
