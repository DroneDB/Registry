using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.DTO;

public class ChangeUserPasswordRequestDto
{

    // CurrentPassword is optional - admins can change any user's password without knowing the current one
    public string? CurrentPassword { get; set; }

    // [ApiController] answers 400 before the action runs when a [Required] member is missing, so it is never null there
    [Required]
    public string NewPassword { get; set; } = null!;

}