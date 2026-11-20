using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Shares;

/// <summary>
/// The chunk session of the caller for one file of a share batch: in the chunk folder of the batch's dataset, named and
/// owned as the dataset chunk uploads name and own theirs (<see cref="ChunkedUploadManager.SessionKey"/>), so another
/// user that sends the same file id reaches a session of their own, never this one.
/// </summary>
public sealed class BatchChunkSessions
{
    private readonly ChunkStore _chunks;
    private readonly IDdbManager _ddbManager;
    private readonly IAuthManager _authManager;

    public BatchChunkSessions(ChunkStore chunks, IDdbManager ddbManager, IAuthManager authManager)
    {
        _chunks = chunks;
        _ddbManager = ddbManager;
        _authManager = authManager;
    }

    /// <summary>The dataset of <paramref name="batch"/>.</summary>
    public IDDB DatasetOf(Batch batch) => _ddbManager.Get(batch.Dataset.Organization.Slug, batch.Dataset.InternalRef);

    /// <summary>The caller's session of <paramref name="fileId"/> in <paramref name="batch"/>, with its status when it exists.</summary>
    public async Task<BatchChunkSession> ForCallerAsync(Batch batch, string fileId)
    {
        var ddb = DatasetOf(batch);
        var root = ChunkedUploadManager.ChunksRoot(ddb);
        var userId = await _authManager.GetCurrentUserId();
        var key = ChunkedUploadManager.SessionKey(userId, fileId);
        return new BatchChunkSession(ddb, root, key, userId, fileId, _chunks.GetStatus(root, key));
    }

    /// <summary>Starts <paramref name="session"/> with <paramref name="manifest"/> (nothing changes when it exists with it).</summary>
    public Task<ChunkStatus> OpenAsync(BatchChunkSession session, ChunkManifest manifest) =>
        _chunks.OpenAsync(session.Root, session.Key, manifest);

    /// <summary>Writes chunk <paramref name="index"/> of <paramref name="session"/>.</summary>
    public Task<ChunkStatus> WriteAsync(BatchChunkSession session, int index, Stream body, CancellationToken ct) =>
        _chunks.WriteAsync(session.Root, session.Key, index, body, ct);

    /// <summary>Moves the complete file of <paramref name="session"/> to <paramref name="destination"/>.</summary>
    public Task<FileHashHint> TakeAsync(BatchChunkSession session, string destination) =>
        _chunks.TakeAsync(session.Root, session.Key, destination);
}

/// <summary>The caller's chunk session of one file of a share batch (<see cref="BatchChunkSessions.ForCallerAsync"/>).</summary>
public sealed record BatchChunkSession(IDDB Ddb, string Root, string Key, string? UserId, string FileId,
    ChunkStatus? Status)
{
    /// <summary>Refuses the session when the caller does not own it (404, as an unknown upload).</summary>
    public void EnsureOwner()
    {
        if (Status is not null)
            ChunkedUploadManager.EnsureOwner(UserId, Status.Manifest, FileId);
    }
}
