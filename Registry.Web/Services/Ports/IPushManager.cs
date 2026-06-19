using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Push workflow interface (init, upload, save meta, commit).
/// </summary>
public interface IPushManager
{
    Task<PushInitResultDto> Init(string orgSlug, string dsSlug, string checksum, StampDto stamp);
    Task Upload(string orgSlug, string dsSlug, string filePath, string token, Stream stream);
    Task SaveMeta(string orgSlug, string dsSlug, string token, string meta);
    Task Commit(string orgSlug, string dsSlug, string token);
}