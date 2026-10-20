using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Push workflow interface (init, upload, save meta, commit).
/// </summary>
/// <summary>
/// A file streamed to the uploads folder of a dataset the current user can write to.
/// </summary>
public sealed record PushUpload(IDDB Ddb, string TempPath, long Bytes);

public interface IPushManager
{
    Task<PushInitResultDto> Init(string orgSlug, string dsSlug, string checksum, StampDto stamp);

    /// <summary>
    /// Streams an uploaded file to a temp file on the dataset volume, after checking write access and
    /// the storage quota (before writing when <paramref name="expectedBytes"/> is known).
    /// </summary>
    Task<PushUpload> StreamUploadAsync(string orgSlug, string dsSlug, Stream source, long? expectedBytes);

    /// <summary>
    /// Moves a file streamed by <see cref="StreamUploadAsync"/> (dataset and access already checked)
    /// to <paramref name="filePath"/> in the folder of the push <paramref name="token"/>.
    /// </summary>
    void Upload(PushUpload upload, string filePath, string token);

    Task SaveMeta(string orgSlug, string dsSlug, string token, string meta);
    Task Commit(string orgSlug, string dsSlug, string token);
}