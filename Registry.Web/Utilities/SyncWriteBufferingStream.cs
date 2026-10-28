#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Utilities;

/// <summary>
/// Write-only adapter for a stream that refuses synchronous writes (an ASP.NET Core response body).
/// <see cref="System.IO.Compression.ZipArchive"/> writes some small records synchronously even
/// through its async API (the data descriptor after each entry on a non-seekable stream): those
/// are kept here and sent ahead of the next asynchronous write, or by <see cref="FlushAsync"/>.
/// The data itself goes through asynchronously, so memory stays at those few records.
/// </summary>
public sealed class SyncWriteBufferingStream(Stream inner) : Stream
{
    private readonly MemoryStream _pending = new();

    public override void Write(byte[] buffer, int offset, int count) => _pending.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _pending.Write(buffer);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await SendPendingAsync(cancellationToken);
        await inner.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await SendPendingAsync(cancellationToken);
        await inner.FlushAsync(cancellationToken);
    }

    // Synchronous flushes only keep the pending bytes: they go out with the next asynchronous call
    public override void Flush()
    {
    }

    private async ValueTask SendPendingAsync(CancellationToken ct)
    {
        if (_pending.Length == 0) return;
        await inner.WriteAsync(_pending.GetBuffer().AsMemory(0, (int)_pending.Length), ct);
        _pending.SetLength(0);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pending.Dispose();
        base.Dispose(disposing);
    }
}
