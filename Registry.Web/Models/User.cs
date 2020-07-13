using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;

namespace Registry.Web.Models
{
    /// <summary>
    /// This class represents an user
    /// </summary>
    public class User : IdentityUser
    {

        public string FirstName { get; set; }
        public string LastName { get; set; }

    }
}
