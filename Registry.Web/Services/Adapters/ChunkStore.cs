#nullable enable
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Registry.Web.Utilities;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Default <see cref="IChunkStore"/>. Layout of an upload under <c>{root}/{fileId}/</c>:
/// <c>manifest.json</c>, <c>data.bin</c> (allocated at the full size; every chunk is written at
/// its offset, so the finished file is a rename away) and <c>received/{index}</c>, one empty
/// marker per stored chunk. The SHA-256 of the prefix of chunks stored in order is kept in memory
/// and fed inline while an in-order chunk streams in; a chunk that arrives early is hashed from
/// disk once the gap before it is filled, and after a restart the hash is rebuilt from disk.
/// </summary>
public sealed partial class ChunkStore : IChunkStore
{
    public const string ManifestFileName = "manifest.json";
    public const string DataFileName = "data.bin";
    public const string ReceivedFolderName = "received";

    private const int BufferSize = 1024 * 1024;

    private sealed class Upload
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public IncrementalHash? Hash;
        public int Hashed; // chunks [0, Hashed) are in Hash
    }

    private readonly ConcurrentDictionary<string, Upload> _uploads = new();

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex FileIdPattern();

    public ChunkStatus Open(string root, string fileId, ChunkManifest manifest)
    {
        Validate(manifest);
        var folder = Folder(root, fileId);
        var manifestPath = Path.Combine(folder, ManifestFileName);

        if (File.Exists(manifestPath))
        {
            var existing = ReadManifest(folder);
            if (existing != manifest)
                throw new ConflictException($"Upload '{fileId}' exists with another file or owner");
            return Status(folder, existing);
        }

        Directory.CreateDirectory(Path.Combine(folder, ReceivedFolderName));
        using (var data = new FileStream(Path.Combine(folder, DataFileName), FileMode.OpenOrCreate, FileAccess.Write,
                   FileShare.ReadWrite))
            data.SetLength(manifest.TotalSize);

        // Written last: an upload is resumable only once its data file exists
        var temp = manifestPath + ".tmp";
        File.WriteAllText(temp, ApiJson.Serialize(manifest));
        File.Move(temp, manifestPath, overwrite: true);

        // Nothing stored yet: the first chunk can be hashed as it streams in
        _uploads[Key(folder)] = new Upload { Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256) };
        return new ChunkStatus(manifest, []);
    }

    public ChunkStatus? GetStatus(string root, string fileId)
    {
        var folder = Folder(root, fileId);
        return File.Exists(Path.Combine(folder, ManifestFileName)) ? Status(folder, ReadManifest(folder)) : null;
    }

    public async Task<ChunkStatus> WriteAsync(string root, string fileId, int index, Stream body,
        CancellationToken ct = default)
    {
        var folder = Folder(root, fileId);
        var manifest = File.Exists(Path.Combine(folder, ManifestFileName))
            ? ReadManifest(folder)
            : throw new NotFoundException($"Unknown upload '{fileId}'");

        if (index < 0 || index >= manifest.TotalChunks)
            throw new BadRequestException($"Chunk {index} is out of range (0..{manifest.TotalChunks - 1})");

        if (File.Exists(Marker(folder, index)))
        {
            // Sent again (the client lost the response): already stored, nothing to rewrite
            await body.CopyToAsync(Stream.Null, ct);
            return Status(folder, manifest);
        }

        var upload = _uploads.GetOrAdd(Key(folder), _ => new Upload());

        // An in-order chunk is hashed as it streams in; any other one is hashed from disk later
        var inline = upload.Lock.Wait(0);
        if (inline && (upload.Hash == null || upload.Hashed != index))
        {
            upload.Lock.Release();
            inline = false;
        }

        try
        {
            var expected = manifest.LengthOf(index);
            long written;
            await using (var data = new FileStream(Path.Combine(folder, DataFileName), FileMode.Open, FileAccess.Write,
                             FileShare.ReadWrite, BufferSize, useAsync: true))
            {
                data.Seek(manifest.OffsetOf(index), SeekOrigin.Begin);
                written = await CopyAsync(body, data, expected, inline ? upload.Hash : null, ct);
                await data.FlushAsync(ct);
            }

            if (written != expected)
                throw new BadRequestException($"Chunk {index} has {written} bytes, expected {expected}");

            if (!inline)
                await upload.Lock.WaitAsync(ct);
            else
                upload.Hashed++;
            inline = true; // the lock is held from here on

            File.Create(Marker(folder, index)).Dispose();
            CatchUp(folder, manifest, upload);
            return Status(folder, manifest);
        }
        catch
        {
            // A chunk that failed half-way left the inline hash with part of it: rebuild from disk
            if (inline) upload.Hash = null;
            throw;
        }
        finally
        {
            if (inline) upload.Lock.Release();
        }
    }

    public async Task<FileHashHint> TakeAsync(string root, string fileId, string destination)
    {
        var folder = Folder(root, fileId);
        var status = GetStatus(root, fileId) ?? throw new NotFoundException($"Unknown upload '{fileId}'");
        if (!status.Complete)
            throw new ConflictException($"Upload '{fileId}' is missing chunks: {string.Join(",", status.Missing)}");

        var upload = _uploads.GetOrAdd(Key(folder), _ => new Upload());
        await upload.Lock.WaitAsync();
        try
        {
            CatchUp(folder, status.Manifest, upload);
            var sha256 = Convert.ToHexStringLower(upload.Hash!.GetHashAndReset());

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(Path.Combine(folder, DataFileName), destination, overwrite: true);
            var info = new FileInfo(destination);
            Forget(folder);
            return new FileHashHint(sha256, info.Length, info.LastWriteTimeUtc);
        }
        finally
        {
            upload.Lock.Release();
        }
    }

    public void Delete(string root, string fileId) => Forget(Folder(root, fileId));

    // Feeds the in-order hash with every stored chunk after the hashed prefix (from disk)
    private static void CatchUp(string folder, ChunkManifest manifest, Upload upload)
    {
        if (upload.Hash == null)
        {
            upload.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            upload.Hashed = 0;
        }

        if (upload.Hashed >= manifest.TotalChunks || !File.Exists(Marker(folder, upload.Hashed)))
            return;

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var data = new FileStream(Path.Combine(folder, DataFileName), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, BufferSize);
            while (upload.Hashed < manifest.TotalChunks && File.Exists(Marker(folder, upload.Hashed)))
            {
                data.Seek(manifest.OffsetOf(upload.Hashed), SeekOrigin.Begin);
                var remaining = manifest.LengthOf(upload.Hashed);
                while (remaining > 0)
                {
                    var read = data.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0) throw new IOException($"Chunk {upload.Hashed} is shorter than expected");
                    upload.Hash.AppendData(buffer, 0, read);
                    remaining -= read;
                }

                upload.Hashed++;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Copies at most expected + 1 bytes (one more is enough to tell the body is too long)
    private static async Task<long> CopyAsync(Stream source, Stream target, long expected, IncrementalHash? hash,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long total = 0;
            int read;
            while (total <= expected &&
                   (read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, expected - total + 1)), ct)) > 0)
            {
                if (total + read > expected)
                    return total + read;
                hash?.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                total += read;
            }

            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Forget(string folder)
    {
        if (_uploads.TryRemove(Key(folder), out var upload))
            upload.Hash?.Dispose();
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
    }

    private static ChunkStatus Status(string folder, ChunkManifest manifest)
    {
        var received = Directory.Exists(Path.Combine(folder, ReceivedFolderName))
            ? Directory.EnumerateFiles(Path.Combine(folder, ReceivedFolderName))
                .Select(f => int.TryParse(Path.GetFileName(f), out var i) ? i : -1)
                .Where(i => i >= 0 && i < manifest.TotalChunks)
                .Order()
                .ToArray()
            : [];
        return new ChunkStatus(manifest, received);
    }

    private static ChunkManifest ReadManifest(string folder) =>
        ApiJson.Deserialize<ChunkManifest>(File.ReadAllText(Path.Combine(folder, ManifestFileName)))
        ?? throw new InvalidOperationException($"Unreadable chunk manifest in '{folder}'");

    private static void Validate(ChunkManifest m)
    {
        if (m.TotalSize <= 0) throw new BadRequestException("The file size must be positive");
        if (m.TotalChunks <= 0) throw new BadRequestException("The number of chunks must be positive");
        if (m.ChunkSize <= 0) throw new BadRequestException("The chunk size must be positive");
        // Every chunk but the last is full, the last one is not empty
        if ((m.TotalChunks - 1) * m.ChunkSize >= m.TotalSize || m.TotalChunks * m.ChunkSize < m.TotalSize)
            throw new BadRequestException(
                $"{m.TotalChunks} chunks of {m.ChunkSize} bytes do not make a file of {m.TotalSize} bytes");
    }

    private static string Folder(string root, string fileId)
    {
        if (fileId == null || !FileIdPattern().IsMatch(fileId))
            throw new BadRequestException("Invalid file id: use 8 to 64 letters, digits, '-' or '_'");
        return Path.Combine(root, fileId);
    }

    private static string Marker(string folder, int index) =>
        Path.Combine(folder, ReceivedFolderName, index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string Key(string folder) => Path.GetFullPath(folder);
}
