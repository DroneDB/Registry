

using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports
{
    public interface IStacManager
    {
        Task<StacCatalogDto> GetCatalog();
    }
}