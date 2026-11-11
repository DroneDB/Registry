using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models;

public class CreateUserRequest
{
    // [ApiController] answers 400 before the action runs when a [Required] member is missing, so it is never null there
    [Required]
    public string UserName { get; set; } = null!;

    [Required]
    public string Password { get; set; } = null!;
        
    public string? Email { get; set; }
        
    public string[]? Roles { get; set; }

}