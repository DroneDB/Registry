using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models
{
    public class ChangeUserPasswordRequest
    {
        [Required]
        public string UserName { get; set; }

        [Required]
        public string CurrentPassword { get; set; }

        [Required]
        public string NewPassword { get; set; }

    }
}