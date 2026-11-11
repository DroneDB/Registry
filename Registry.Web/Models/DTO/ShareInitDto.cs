using System.ComponentModel.DataAnnotations;
using Registry.Ports.DroneDB;
using Registry.Web.Utilities;

namespace Registry.Web.Models.DTO;

public class ShareInitDto
{
    public string? Tag { get; set; }

    /// <summary>
    /// Organization slug to create a new dataset in. Used when Tag is null.
    /// Allows callers to specify a destination org without needing to supply
    /// a full "org/dataset" tag (the dataset slug is derived from <see cref="DatasetName"/>).
    /// </summary>
    public string? OrgSlug { get; set; }

    [MaxLength(SlugRules.NameMaxLength, ErrorMessage = SlugRules.NameTooLongMessage)]
    public string? DatasetName { get; set; }

    /// <summary>
    /// The visibility of the dataset the request creates (FR-UPL-02, D-009). The Hub sends Private unless the user chose
    /// another one; a request without it (<c>ddb share</c>, older clients) keeps <see cref="Visibility.Public"/>.
    /// </summary>
    public Visibility? Visibility { get; set; }
}
