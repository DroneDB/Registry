using System;
using System.ComponentModel.DataAnnotations;
using Registry.Common;

namespace Registry.Web.Data.Models
{
    public class Entry
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string Path { get; set; }
        
        [Required]
        public string Hash { get; set; }
        [Required]
        public EntryType Type { get; set; }
        [Required]
        public long Size { get; set; }
        [Required]
        public DateTime AddedOn { get; set; }

        [Required]
        public Batch Batch { get; set; }
    }
}