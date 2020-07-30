using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Registry.Web.Data.Models;

namespace Registry.Web.Models.DTO
{
    public class OrganizationDto
    {
        [Required]
        public string Id { get; set; }

        [Required]
        public string Name { get; set; }
        public string Description { get; set; }
        public DateTime CreationDate { get; set; }
        public string Owner { get; set; }

        public bool IsPublic { get; set; }


    }

}
