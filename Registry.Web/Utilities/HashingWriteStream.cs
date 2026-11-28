using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Utilities;

/// <summary>
/// Write-only decorator that computes the SHA-256 of everything written through it:
/// an upload is hashed while it streams to disk, so indexing it does not read the whole file
/// again after the last byte. The digest equals DroneDB's <c>Hash::fileSHA256</c> of the file.
/// </summary>
public sealed class HashingWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string? _digest;

    public HashingWriteStream(Stream inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>Bytes written so far.</summary>
    public long BytesWritten { get; private set; }

    /// <summary>
    /// Lowercase hex SHA-256 of the bytes written. Ends hashing: no write is accepted afterwards.
    /// </summary>
    public string GetHexDigest()
    {
        _digest ??= Convert.ToHexStringLower(_hash.GetHashAndReset());
        return _digest;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => _digest == null;
    public override long Length => BytesWritten;

    public override long Position
    {
        get => BytesWritten;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Append(buffer);
        _inner.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Append(buffer.Span);
        await _inner.WriteAsync(buffer, cancellationToken);
    }

    private void Append(ReadOnlySpan<byte> buffer)
    {
        if (_digest != null)
            throw new InvalidOperationException("The digest was already computed");
        _hash.AppendData(buffer);
        BytesWritten += buffer.Length;
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        _hash.Dispose();
        await base.DisposeAsync();
    }
}
