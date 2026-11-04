using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.DTO;

public class EditUserRequestDto
{
    // [ApiController] answers 400 before the action runs when a [Required] member is missing, so it is never null there
    [Required]
    public string Email { get; set; } = null!;
    
    [Required]
    public string[] Roles { get; set; } = null!;
}