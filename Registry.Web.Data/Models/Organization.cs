using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Text;

namespace Registry.Web.Data.Models
{
    public class Organization
    {
        [Key]
        [MaxLength(128)]
        public string Slug { get; set; }
        [Required]
        public string Name { get; set; }
        public string Description { get; set; }
        [Required]
        public DateTime CreationDate { get; set; }
        public string OwnerId { get; set; }

        [Required]
        public bool IsPublic { get; set; }

        public virtual ICollection<Dataset> Datasets { get; set; }
        public virtual ICollection<OrganizationUser> Users { get; set; }
    }

    public class OrganizationUser
    {
        [Required]
        public Organization Organization { get; set; }
        [Required]
        public string UserId { get; set; }

        public string OrganizationSlug { get; set; }

        // public AccessCapability DatasetCapability { get; set; }
    }

    /* TBA
    [Flags]
    public enum Capability
    {
        CanView = 0,
        CanAdd = 1,
        CanEdit = 2,
        CanDelete = 4        
    }*/
}
