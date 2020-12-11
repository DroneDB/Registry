using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Registry.Web.Data.Models
{
    public class Dataset
    {
        [MaxLength(128)]
        [Required]
        public string Slug { get; set; }
        public Guid InternalRef { get; set; }

        [Key]
        public int Id { get; set; }

        [Required]
        public string Name { get; set; }
        public string Description { get; set; }
        [Required]
        public DateTime CreationDate { get; set; }
        public string License { get; set; }
        public long Size { get; set; }
        public int ObjectsCount { get; set; }
        public DateTime LastEdit { get; set; }
        public string Meta { get; set; }

        public string PasswordHash { get; set; }
        
        [Required]
        public bool IsPublic { get; set; }

        [Required]
        public Organization Organization { get; set; }

        public virtual ICollection<Batch> Batches { get; set; }

        public virtual ICollection<DownloadPackage> DownloadPackages { get; set; }

    }
}