namespace Registry.Web.Models.DTO;

public class ShareInitDto
{
    public string Tag { get; set; }

    /// <summary>
    /// Organization slug to create a new dataset in. Used when Tag is null.
    /// Allows callers to specify a destination org without needing to supply
    /// a full "org/dataset" tag (the dataset slug is auto-generated).
    /// </summary>
    public string OrgSlug { get; set; }

    public string DatasetName { get; set; }
}