using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.DTO;

public class CreateRoleRequestDto
{
    // [ApiController] answers 400 before the action runs when a [Required] member is missing, so it is never null there
    [Required]
    public string RoleName { get; set; } = null!;
}