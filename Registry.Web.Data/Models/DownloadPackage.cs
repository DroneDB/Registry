using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Registry.Web.Data.Models
{
    public class DownloadPackage
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        public DateTime CreationDate { get; set; }
        
        /// <summary>
        /// Link expiration date, if null the package allows only one download
        /// </summary>
        public DateTime? ExpirationDate { get; set; }

        [Required]
        public Dataset Dataset { get; set; }

        public string[] Paths { get; set; }

        [Required]
        public string UserName { get; set; }

        [Required]
        public bool IsPublic { get; set; }

    }
}
