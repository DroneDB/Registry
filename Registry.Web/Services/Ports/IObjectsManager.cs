using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports
{
    public interface IObjectsManager
    {
        Task<IEnumerable<ObjectDto>> List(string orgSlug, string dsSlug, string path);
        Task<ObjectRes> Get(string orgSlug, string dsSlug, string path);
        Task<UploadedObjectDto> AddNew(string orgSlug, string dsSlug, string path, byte[] data);
        Task Delete(string orgSlug, string dsSlug, string path);

        Task DeleteAll(string orgSlug, string dsSlug);
    }
}
