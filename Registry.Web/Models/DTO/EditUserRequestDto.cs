using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.DTO;

public class EditUserRequestDto
{
    [Required]
    public string Email { get; set; }
    
    [Required]
    public string[] Roles { get; set; }
}