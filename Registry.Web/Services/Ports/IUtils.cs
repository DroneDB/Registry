using System.Threading.Tasks;
using Registry.Web.Data.Models;

namespace Registry.Web.Services.Ports
{
    public interface IUtils
    {
        bool IsOrganizationNameValid(string name);

        Task<Organization> GetOrganizationAndCheck(string orgId, bool safe = false);
        Task<Dataset> GetDatasetAndCheck(string orgId, string dsId, bool safe = false);
        bool IsDatasetNameValid(string name);
    }
}
