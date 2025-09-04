using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.DTO;

public class ChangeUserPasswordRequestDto
{

    // CurrentPassword is optional - admins can change any user's password without knowing the current one
    public string CurrentPassword { get; set; }

    [Required]
    public string NewPassword { get; set; }

}