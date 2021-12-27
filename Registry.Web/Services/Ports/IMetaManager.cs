using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports
{
    public interface IMetaManager
    {
        public Task<MetaDto> Add(string orgSlug, string dsSlug, string key, string data, string path = null);
        public Task<MetaDto> Set(string orgSlug, string dsSlug, string key, string data, string path = null);
        public Task<int> Remove(string orgSlug, string dsSlug, string id);
        public Task<JToken> Get(string orgSlug, string dsSlug, string key, string path = null);
        public Task<int> Unset(string orgSlug, string dsSlug, string key, string path = null);
        public Task<IEnumerable<MetaListItemDto>> List(string orgSlug, string dsSlug, string path = null);
        public Task<IEnumerable<MetaDumpDto>> Dump(string orgSlug, string dsSlug, string ids = null);
    }
}