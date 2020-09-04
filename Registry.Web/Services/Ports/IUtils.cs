using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;

namespace Registry.Web.Services.Ports
{
    public interface IUtils
    {
        bool IsSlugValid(string name);

        string MakeSlug(string name);

        string DatasetSlugFromTag(string tag);

        string OrganizationSlugFromTag(string tag);

        Task<Organization> GetOrganizationAndCheck(string orgSlug, bool safe = false);
        Task<Dataset> GetDatasetAndCheck(string orgSlug, string dsSlug, bool safe = false);
    }
}
