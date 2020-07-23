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
        public string Id { get; set; }
        [Required]
        public string Name { get; set; }
        public string Description { get; set; }
        [Required]
        public DateTime CreationDate { get; set; }
        public string OwnerId { get; set; }
        public bool IsPublic { get; set; }

        public List<Models.Dataset> Datasets { get; set; }
    }
}
