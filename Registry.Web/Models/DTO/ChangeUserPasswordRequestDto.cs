using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.DTO
{
    public class ChangeUserPasswordRequestDto
    {
        [Required]
        public string UserName { get; set; }

        [Required]
        public string CurrentPassword { get; set; }

        [Required]
        public string NewPassword { get; set; }

    }
}