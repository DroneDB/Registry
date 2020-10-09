using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports
{
    public interface IUtils
    {

        Task<Organization> GetOrganizationAndCheck(string orgSlug, bool safe = false);
        Task<Dataset> GetDatasetAndCheck(string orgSlug, string dsSlug, bool safe = false);
        string GetFreeOrganizationSlug(string orgName);
    }
}
