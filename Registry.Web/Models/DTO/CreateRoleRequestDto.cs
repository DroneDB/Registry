using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.DTO;

public class CreateRoleRequestDto
{
    [Required]
    public string RoleName { get; set; }
}