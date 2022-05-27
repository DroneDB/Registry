using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models
{
    public class CreateUserRequest
    {
        [Required]
        public string UserName { get; set; }

        [Required]
        public string Password { get; set; }
        
        public string Email { get; set; }   
        
        public string[] Roles { get; set; }

    }
}