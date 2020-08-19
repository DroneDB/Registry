using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports
{
    public interface IShareManager
    {
        public Task<string> Initialize(ShareInitDto parameters);
        public Task Upload(string token, string path, byte[] data);
        public Task Commit(string token);
        Task<IEnumerable<BatchDto>> List(string orgId, string dsSlug);
    }
}
