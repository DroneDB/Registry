using System.Threading.Tasks;
using Registry.Web.Data.Models;

namespace Registry.Web.Services.Ports
{
    public interface IUtils
    {
        bool IsOrganizationNameValid(string name);

        Task<Organization> GetOrganizationAndCheck(string orgId);
        Task<Dataset> GetDatasetAndCheck(string orgId, string dsId);
        bool IsDatasetNameValid(string name);
    }
}
