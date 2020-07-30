using System.Collections.Generic;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports
{
    public interface IDatasetsManager
    {
        public Task<IEnumerable<DatasetDto>> List(string orgId);
        public Task<DatasetDto> Get(string orgId, string ds);
        public Task<DatasetDto> AddNew(string orgId, DatasetDto dataset);
        public Task Edit(string orgId, string ds, DatasetDto dataset);
        public Task Delete(string orgId, string ds);
    }
}
