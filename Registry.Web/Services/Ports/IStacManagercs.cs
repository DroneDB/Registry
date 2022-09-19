

using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports
{
    public interface IStacManager
    {
        Task<StacCatalogDto> GetCatalog();
        Task<JToken> GetStacChild(string orgSlug, string dsSlug, string path = null);
        Task ClearCache(Dataset ds);
    }
}