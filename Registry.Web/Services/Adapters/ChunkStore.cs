using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Utilities;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Durable storage of resumable chunked uploads. Everything that defines an upload is
/// on disk (manifest, chunk markers, one preallocated data file the chunks are written into at
/// their offset), so chunks can arrive in any request, in any order, on any instance sharing the
/// storage, and across a restart. The SHA-256 is computed as the chunks arrive in order, so
/// taking the finished file needs no extra pass over it.
/// </summary>
public sealed partial class ChunkStore
{
    public const string ManifestFileName = "manifest.json";
    public const string DataFileName = "data.bin";
    public const string ReceivedFolderName = "received";

    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// How long an upload folder lives after its last chunk: the cleanup removes it then (§8.3). A constant, not a
    /// setting (§11): a user can come back to a paused upload within the week.
    /// </summary>
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromDays(7);

    [SuppressMessage("Design", "CA1001", Justification =
        "Lock is only awaited and released, never asked for its AvailableWaitHandle, so it holds nothing to " +
        "dispose; disposing it when the upload is dropped could race with a chunk write still waiting on it.")]
    private sealed class Upload
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public IncrementalHash? Hash;
        public int Hashed; // chunks [0, Hashed) are in Hash
    }

    private readonly ConcurrentDictionary<string, Upload> _uploads = new();

    /// <summary>How many uploads have a state in memory (tests: a removed upload leaves none).</summary>
    internal int UploadCount => _uploads.Count;

    /// <summary>Tests: runs in a chunk write right after it read the manifest of its upload folder.</summary>
    internal Action<string>? AfterManifestRead { get; set; }

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex FileIdPattern();

    /// <summary>
    /// Starts an upload, or resumes it when <paramref name="fileId"/> already exists with the same
    /// manifest. A folder without a manifest (an upload whose start or removal was interrupted) starts empty: its chunk
    /// markers are dropped, so no chunk of another upload counts as stored.
    /// </summary>
    /// <exception cref="Registry.Web.Exceptions.ConflictException">The id exists with another manifest.</exception>
    public ChunkStatus Open(string root, string fileId, ChunkManifest manifest)
    {
        Validate(manifest);
        var folder = Folder(root, fileId);

        if (TryResume(folder, fileId, manifest) is { } resumed)
            return resumed;

        // A new upload: drop first the states of the uploads removed without this store (by another instance's
        // cleanup, with their dataset), so they do not pile up in a process that never runs the cleanup
        PruneRemoved();

        // Under the upload's lock: a removal (Delete) or another start of the same upload is not half-way
        var upload = Lock(Key(folder));
        try
        {
            if (TryResume(folder, fileId, manifest) is { } started)
                return started;

            var received = Path.Combine(folder, ReceivedFolderName);
            if (Directory.Exists(received))
                Directory.Delete(received, true);
            Directory.CreateDirectory(received);
            using (var data = new FileStream(Path.Combine(folder, DataFileName), FileMode.OpenOrCreate, FileAccess.Write,
                       FileShare.ReadWrite))
                data.SetLength(manifest.TotalSize);

            // Written last: an upload is resumable only once its data file exists
            var manifestPath = Path.Combine(folder, ManifestFileName);
            var temp = manifestPath + ".tmp";
            File.WriteAllText(temp, ApiJson.Serialize(manifest));
            File.Move(temp, manifestPath, overwrite: true);

            // Nothing stored yet: the first chunk can be hashed as it streams in
            upload.Hash?.Dispose();
            upload.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            upload.Hashed = 0;
            return new ChunkStatus(manifest, []);
        }
        finally
        {
            upload.Lock.Release();
        }
    }

    // The state of an upload that exists with the same manifest; null when there is none
    private static ChunkStatus? TryResume(string folder, string fileId, ChunkManifest manifest)
    {
        if (!File.Exists(Path.Combine(folder, ManifestFileName)))
            return null;

        var existing = ReadManifest(folder);
        if (existing != manifest)
            throw new ConflictException($"Upload '{fileId}' exists with another file or owner");
        return Status(folder, existing);
    }

    /// <summary>Whether <paramref name="fileId"/> is an id the store accepts (8 to 64 letters, digits, '-' or '_').</summary>
    public static bool IsValidFileId(string? fileId) => fileId != null && FileIdPattern().IsMatch(fileId);

    /// <summary>
    /// Every upload of <paramref name="root"/> whose manifest can be read, with its state: the open uploads of every
    /// owner (the caller selects its own by <see cref="ChunkManifest.UserId"/>). An unreadable one is skipped.
    /// </summary>
    public IReadOnlyList<ChunkStatus> List(string root)
    {
        if (!Directory.Exists(root))
            return [];

        string[] folders;
        try
        {
            folders = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed meanwhile (its dataset was deleted) or unreadable: no upload of it can be listed
            return [];
        }

        var uploads = new List<ChunkStatus>();
        foreach (var folder in folders)
        {
            try
            {
                if (File.Exists(Path.Combine(folder, ManifestFileName)))
                    uploads.Add(Status(folder, ReadManifest(folder)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                           or System.Text.Json.JsonException)
            {
                // Removed or being written meanwhile: not an open upload
            }
        }

        return uploads;
    }

    /// <summary>
    /// Removes the stored chunks of an upload (its Cancel). Returns false when the upload is unknown. It waits for a
    /// chunk of the upload that is being stored; a chunk that arrives after it is refused as an unknown upload.
    /// </summary>
    public async Task<bool> DeleteAsync(string root, string fileId)
    {
        var folder = Folder(root, fileId);
        var upload = await LockAsync(Key(folder));
        try
        {
            if (!Directory.Exists(folder))
            {
                // Nothing to remove: do not keep the entry this call added
                DropIfRemoved(folder, upload);
                return false;
            }

            Forget(folder);
            return true;
        }
        finally
        {
            upload.Lock.Release();
        }
    }

    /// <summary>
    /// Removes the upload folder <paramref name="folder"/> (any folder of a chunks root) when
    /// <paramref name="abandoned"/> still holds under the upload's lock, with the state of the upload in memory: the
    /// cleanup's removal of an abandoned upload.
    /// </summary>
    /// <returns>Whether the folder was removed.</returns>
    public bool RemoveAbandoned(string folder, Func<string, bool> abandoned)
    {
        var upload = Lock(Key(folder));
        try
        {
            if (!Directory.Exists(folder) || !abandoned(folder))
            {
                DropIfRemoved(folder, upload);
                return false;
            }

            Forget(folder);
            return true;
        }
        finally
        {
            upload.Lock.Release();
        }
    }

    /// <summary>
    /// Drops the state in memory of every upload whose folder is gone without this store (its dataset was deleted,
    /// another instance removed it). An upload in use is left to the request that uses it.
    /// </summary>
    /// <returns>How many states were dropped.</returns>
    public int PruneRemoved()
    {
        var dropped = 0;
        foreach (var (folder, upload) in _uploads)
        {
            if (File.Exists(Path.Combine(folder, ManifestFileName)) || !upload.Lock.Wait(0))
                continue;
            try
            {
                if (DropIfRemoved(folder, upload))
                    dropped++;
            }
            finally
            {
                upload.Lock.Release();
            }
        }

        return dropped;
    }

    /// <summary>The state of an upload, or null when unknown.</summary>
    public ChunkStatus? GetStatus(string root, string fileId)
    {
        var folder = Folder(root, fileId);
        return File.Exists(Path.Combine(folder, ManifestFileName)) ? Status(folder, ReadManifest(folder)) : null;
    }

    /// <summary>
    /// Stores chunk <paramref name="index"/> from <paramref name="body"/>. A chunk already stored
    /// is accepted again without being rewritten.
    /// </summary>
    /// <exception cref="Registry.Web.Exceptions.BadRequestException">The body is not the chunk's size.</exception>
    public async Task<ChunkStatus> WriteAsync(string root, string fileId, int index, Stream body,
        CancellationToken ct = default)
    {
        var folder = Folder(root, fileId);
        var manifest = TryReadManifest(folder) ?? throw new NotFoundException($"Unknown upload '{fileId}'");
        AfterManifestRead?.Invoke(folder);

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
        var inline = upload.Lock.Wait(0, CancellationToken.None);
        if (inline && (!IsLive(folder, upload) || upload.Hash == null || upload.Hashed != index))
        {
            upload.Lock.Release();
            inline = false;
        }

        try
        {
            var expected = manifest.LengthOf(index);
            long written;
            FileStream data;
            try
            {
                // Shared for removal too: a Cancel (Directory.Delete) of the upload while a chunk is written outside the
                // lock does not fail on a host that refuses to delete an open file (Windows)
                data = new FileStream(Path.Combine(folder, DataFileName), FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Removed (its Cancel) since the manifest was read: the state this call may have added goes too
                if (!inline)
                    await upload.Lock.WaitAsync(CancellationToken.None);
                inline = true; // the lock is held from here on
                DropIfRemoved(folder, upload);
                throw new NotFoundException($"Unknown upload '{fileId}'");
            }

            await using (data)
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

            // Removed while the chunk was written (the bytes went to the removed data file), or before this call
            // took the upload's state: nothing to mark, and no state of it is kept
            if (!IsLive(folder, upload))
            {
                DropIfRemoved(folder, upload);
                throw new NotFoundException($"Unknown upload '{fileId}'");
            }

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

    /// <summary>
    /// Moves the finished file to <paramref name="destination"/> and forgets the upload.
    /// </summary>
    /// <returns>The file's hash hint (its SHA-256, size and write time).</returns>
    /// <exception cref="Registry.Web.Exceptions.RecoverableConflictException">Chunks are missing.</exception>
    public async Task<FileHashHint> TakeAsync(string root, string fileId, string destination)
    {
        var folder = Folder(root, fileId);
        var status = GetStatus(root, fileId) ?? throw new NotFoundException($"Unknown upload '{fileId}'");
        // Recoverable: once the client sends the missing chunks, the same commit succeeds (noRetry stays false)
        if (!status.Complete)
            throw new RecoverableConflictException(
                $"Upload '{fileId}' is missing chunks: {string.Join(",", status.Missing)}");

        var upload = await LockAsync(Key(folder));
        try
        {
            // Removed (its Cancel) since its state was read
            if (!File.Exists(Path.Combine(folder, ManifestFileName)))
            {
                DropIfRemoved(folder, upload);
                throw new NotFoundException($"Unknown upload '{fileId}'");
            }

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

    // Whether the upload of `folder` still exists and `upload` is its state (a removal drops the state; a new start of
    // the same id makes another one). Asked under the lock of `upload`.
    private bool IsLive(string folder, Upload upload) =>
        _uploads.TryGetValue(Key(folder), out var live) && ReferenceEquals(live, upload) &&
        File.Exists(Path.Combine(folder, ManifestFileName));

    // The manifest, or null when there is none (never started, or removed meanwhile)
    private static ChunkManifest? TryReadManifest(string folder)
    {
        try
        {
            return File.Exists(Path.Combine(folder, ManifestFileName)) ? ReadManifest(folder) : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    // Under the upload's lock (or for a folder no request can reach)
    private void Forget(string folder)
    {
        if (_uploads.TryRemove(Key(folder), out var upload))
            Dispose(upload);
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
    }

    // Under the lock of `upload`: drops it when its upload is gone (no manifest), unless another state replaced it.
    // Nothing else would remove the state that a request added after the upload was removed.
    private bool DropIfRemoved(string folder, Upload upload)
    {
        if (File.Exists(Path.Combine(folder, ManifestFileName)) ||
            !_uploads.TryRemove(new KeyValuePair<string, Upload>(Key(folder), upload)))
            return false;
        Dispose(upload);
        return true;
    }

    private static void Dispose(Upload upload)
    {
        upload.Hash?.Dispose();
        upload.Hash = null;
    }

    // The state of `key`, locked. A state dropped while this call waited for its lock is no longer the upload's: the
    // current one is locked instead, so two callers never hold two states of one upload.
    private Upload Lock(string key)
    {
        while (true)
        {
            var upload = _uploads.GetOrAdd(key, _ => new Upload());
            upload.Lock.Wait();
            if (_uploads.TryGetValue(key, out var live) && ReferenceEquals(live, upload))
                return upload;
            upload.Lock.Release();
        }
    }

    private async Task<Upload> LockAsync(string key)
    {
        while (true)
        {
            var upload = _uploads.GetOrAdd(key, _ => new Upload());
            await upload.Lock.WaitAsync();
            if (_uploads.TryGetValue(key, out var live) && ReferenceEquals(live, upload))
                return upload;
            upload.Lock.Release();
        }
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
