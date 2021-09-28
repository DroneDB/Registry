using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
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

        /*
         *  When updating the entity and changing items in the dictionary,
         *  the EF change tracking does not pick up on the fact that the dictionary was updated,
         *  so you will need to explicitly call the Update method on the DbSet<> to set the entity
         *  to modified in the change tracker.
         */
        public Dictionary<string, object> Metadata { get; set; } = new();
    }
}
