using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Registry.Adapters.DroneDB.Models;

namespace Registry.Web.Services.Ports
{
    public interface IMetaManager
    {
        public Task<Meta> Add(string orgSlug, string dsSlug, string key, string data, string path = null);
        public Task<Meta> Set(string orgSlug, string dsSlug, string key, string data, string path = null);
        public Task<int> Remove(string orgSlug, string dsSlug, string id);
        public Task<JToken> Get(string orgSlug, string dsSlug, string key, string path = null);
        public Task<int> Unset(string orgSlug, string dsSlug, string key, string path = null);
        public Task<MetaListItem[]> List(string orgSlug, string dsSlug, string path = null);
    }
}