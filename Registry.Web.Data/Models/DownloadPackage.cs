using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Registry.Web.Data.Models
{
    public class DownloadPackage
    {
        [Required]
        public DateTime CreationDate { get; set; }
        
        /// <summary>
        /// Link expiration date, if null the package allows only one download
        /// </summary>
        public DateTime? ExpirationDate { get; set; }

        [Required]
        public Dataset Dataset { get; set; }

        [Required]
        public string[] Queries { get; set; }

        [Required]
        public string UserName { get; set; }

    }
}
