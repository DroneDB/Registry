using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.DTO;

public class EditUserRequestDto
{
    /// <summary>
    /// The address of the user (FR-ADM-07): optional. Omitted (or null) keeps the current address, empty removes it,
    /// anything else must be one valid address.
    /// </summary>
    public string? Email { get; set; }

    // [ApiController] answers 400 before the action runs when a [Required] member is missing, so it is never null there
    [Required]
    public string[] Roles { get; set; } = null!;
}