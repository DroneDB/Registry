using System.IO;
using System.Threading.Tasks;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Push;

namespace Registry.Web.Services.Managers;

/// <summary>
/// The offline-first push (<see cref="IPushManager"/>, what <c>PushController</c> calls) over the push services RQ-12
/// split it into (14 parameters before): the session, the uploads, the commit.
/// </summary>
public class PushManager : IPushManager
{
    private readonly IPushSessions _sessions;
    private readonly IPushUploads _uploads;
    private readonly IPushCommit _commit;

    public PushManager(IPushSessions sessions, IPushUploads uploads, IPushCommit commit)
    {
        _sessions = sessions;
        _uploads = uploads;
        _commit = commit;
    }

    public Task<PushInitResultDto> Init(string orgSlug, string dsSlug, string checksum, StampDto stamp) =>
        _sessions.Init(orgSlug, dsSlug, checksum, stamp);

    public Task<PushUpload> StreamUploadAsync(string orgSlug, string dsSlug, Stream source, long? expectedBytes) =>
        _uploads.StreamUploadAsync(orgSlug, dsSlug, source, expectedBytes);

    public Task UploadAsync(PushUpload upload, string filePath, string token) =>
        _uploads.UploadAsync(upload, filePath, token);

    public Task SaveMeta(string orgSlug, string dsSlug, string token, string meta) =>
        _uploads.SaveMeta(orgSlug, dsSlug, token, meta);

    public Task Commit(string orgSlug, string dsSlug, string token) => _commit.Commit(orgSlug, dsSlug, token);
}
