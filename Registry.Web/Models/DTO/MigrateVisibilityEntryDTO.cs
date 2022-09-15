using Registry.Ports.DroneDB.Models;

namespace Registry.Web.Models.DTO;

public class MigrateVisibilityEntryDTO
{
    public string OrganizationSlug { get; set; }
    public string DatasetSlug { get; set; }
    public object IsPublic { get; set; }
    public Visibility Visibility { get; set; }
}