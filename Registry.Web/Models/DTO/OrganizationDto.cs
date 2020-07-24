using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Registry.Web.Data.Models;

namespace Registry.Web.Models.DTO
{
    public class OrganizationDto : Dto<Organization>
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public DateTime CreationDate { get; set; }
        public string Owner { get; set; }

        public bool IsPublic { get; set; }

        public OrganizationDto()
        {

        }

        public OrganizationDto(Organization org)
        {
            Id = org.Id;
            Name = org.Name;
            Description = org.Description;
            CreationDate = org.CreationDate;
            Owner = org.OwnerId;
            IsPublic = org.IsPublic;
        }

        public override Organization ToEntity()
        {
            return new Organization
            {
                Id = Id,
                Name = Name,
                Description = Description,
                CreationDate = CreationDate,
                OwnerId = Owner,
                IsPublic = IsPublic,
        };
        }
    }

}
