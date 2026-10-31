using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Push;

namespace Registry.Web.Services.Managers;

/// <summary>
/// The offline-first push (<see cref="PushManager"/>, what <c>PushController</c> calls) over the push services it is split into: the session, the uploads, the commit.
/// </summary>
public class PushManager
{
    private readonly PushSessions _sessions;
    private readonly PushUploads _uploads;
    private readonly PushCommit _commit;

    public PushManager(PushSessions sessions, PushUploads uploads, PushCommit commit)
    {
        _sessions = sessions;
        _uploads = uploads;
        _commit = commit;
    }

    public Task<PushInitResultDto> Init(string orgSlug, string dsSlug, string checksum, StampDto stamp, CancellationToken ct = default) =>
        _sessions.Init(orgSlug, dsSlug, checksum, stamp, ct: ct);

    /// <summary>
    /// Streams an uploaded file to a temp file on the dataset volume, after checking write access and
    /// the storage quota (before writing when <paramref name="expectedBytes"/> is known).
    /// </summary>
    public Task<PushUpload> StreamUploadAsync(string orgSlug, string dsSlug, Stream source, long? expectedBytes, CancellationToken ct = default) =>
        _uploads.StreamUploadAsync(orgSlug, dsSlug, source, expectedBytes, ct: ct);

    /// <summary>
    /// Moves a file streamed by <see cref="StreamUploadAsync"/> (dataset and access already checked)
    /// to <paramref name="filePath"/> in the folder of the push <paramref name="token"/>, once its
    /// streamed hash matches the one the push's delta gives that path.
    /// </summary>
    /// <exception cref="Exceptions.BadRequestException">The delta adds no file at that path, or the
    /// bytes received are not the delta's (the file is not moved).</exception>
    public Task UploadAsync(PushUpload upload, string? filePath, string? token, CancellationToken ct = default) =>
        _uploads.UploadAsync(upload, filePath, token, ct: ct);

    public Task SaveMeta(string orgSlug, string dsSlug, string token, string meta, CancellationToken ct = default) =>
        _uploads.SaveMeta(orgSlug, dsSlug, token, meta, ct: ct);

    public Task Commit(string orgSlug, string dsSlug, string token, CancellationToken ct = default) => _commit.Commit(orgSlug, dsSlug, token, ct: ct);
}
