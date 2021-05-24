using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports
{
    public interface IUtils
    {

        Task<Organization> GetOrganization(string orgSlug, bool safe = false, bool checkOwnership = true);
        Task<Dataset> GetDataset(string orgSlug, string dsSlug, bool safe = false, bool checkOwnership = true);

        string GetFreeOrganizationSlug(string orgName);
        string GenerateDatasetUrl(Dataset dataset);
    }
}
