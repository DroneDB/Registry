using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports
{
    public interface IPushManager
    {
        Task<PushInitResultDto> Init(string orgSlug, string dsSlug, Stream stream);
        Task Upload(string orgSlug, string dsSlug, string filePath, Stream stream);
        Task Commit(string orgSlug, string dsSlug);

        Task Clean(string orgSlug, string dsSlug);
    }

}
