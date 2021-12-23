using System.Collections.Generic;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports
{
    public interface IDatasetsManager
    {
        public Task<IEnumerable<DatasetDto>> List(string orgSlug);
        public Task<DatasetDto> Get(string orgSlug, string dsSlug);
        public Task<EntryDto[]> GetEntry(string orgSlug, string dsSlug);
        public Task<DatasetDto> AddNew(string orgSlug, DatasetDto dataset);
        public Task Edit(string orgSlug, string dsSlug, DatasetDto dataset);
        public Task Delete(string orgSlug, string dsSlug);

        public Task Rename(string orgSlug, string dsSlug, string newSlug);
        Task<Dictionary<string, object>> ChangeAttributes(string orgSlug, string dsSlug, Dictionary<string, object> attributes);
        public Task<StampDto> GetStamp(string orgSlug, string dsSlug);
    }
}
