using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Data.Models;

namespace Registry.Web.Models.DTO
{
    public class DatasetDto
    {
        
        [Required]
        public string Slug { get; set; }
        [Required]
        public DateTime CreationDate { get; set; }
        public Dictionary<string, object> Properties { get; set; }

        public long Size { get; set; }

    }
    
    public class DatasetNewDto
    {
        [Required]
        public string Slug { get; set; }

        public string Name { get; set; }
        public bool? IsPublic { get; set; }

    }
    
    public class DatasetEditDto
    {

        public string Name { get; set; }
        public bool? IsPublic { get; set; }

    }
}
