using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Models.DTO
{
    public class UserDto
    {
        public string UserName { get; set; }
        public string Email { get; set; }
        
        public string[] Roles { get; set; }
        public string[] Organizations { get; set; }
    }

}
