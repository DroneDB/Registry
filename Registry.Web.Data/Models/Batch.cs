using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Text;

namespace Registry.Web.Data.Models
{
    public class Batch
    {
        [Key]
        public string Token { get; set; }

        [Required]
        public Dataset Dataset { get; set; }

        [Required]
        public string UserName { get; set; }
        
        [Required]
        public DateTime Start { get; set; }
        public DateTime? End { get; set; }

        public BatchStatus Status { get; set; }
        
        public virtual ICollection<Entry> Entries { get; set; }
        
    }

    public enum BatchStatus
    {
        Running, Committed, Rolledback
    }
}
