using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Models.DTO;

public class StreamableFileDescriptor
{
    private readonly Func<Stream, CancellationToken, Task> _copyTo;

    public StreamableFileDescriptor(Func<Stream, CancellationToken, Task> copyTo, string name, string contentType)
    {
        _copyTo = copyTo;
        Name = name;
        ContentType = contentType;
    }

    public string Name { get; }
    public string ContentType { get; }

    public async Task CopyToAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        await _copyTo(stream, cancellationToken);
    }

}