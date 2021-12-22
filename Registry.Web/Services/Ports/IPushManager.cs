using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Registry.Adapters.DroneDB.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports
{
    public interface IPushManager
    {
        Task<PushInitResultDto> Init(string orgSlug, string dsSlug, string checksum, Stamp stamp);
        Task Upload(string orgSlug, string dsSlug, string filePath, string token, Stream stream);
        Task Commit(string orgSlug, string dsSlug, string token);

        Task Clean(string orgSlug, string dsSlug);
    }

}
