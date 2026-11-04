using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Ports;

public interface IThumbnailGenerator
{
    /// <summary>
    /// Writes a <paramref name="size"/> px thumbnail of <paramref name="filePath"/> to <paramref name="output"/>.
    /// <paramref name="ct"/> stops the work (the native render through its cancel token, a remote request) and
    /// surfaces as an <see cref="System.OperationCanceledException"/>, never as a generation failure.
    /// </summary>
    public Task GenerateThumbnailAsync(string filePath, int size, Stream output, CancellationToken ct = default);
}
