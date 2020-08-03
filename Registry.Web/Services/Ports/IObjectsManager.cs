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
        Task<IEnumerable<ObjectDto>> List(string orgId, string dsId, string path);
        Task<ObjectRes> Get(string orgId, string dsId, string path);
        Task<ObjectDto> AddNew(string orgId, string dsId, string path);
        Task Delete(string orgId, string dsId, string path);

        Task DeleteAll(string orgId, string dsId);
    }
}
