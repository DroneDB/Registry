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

        public virtual ICollection<Entry> Entries { get; set; }
        
    }

    public class Entry
    {
        [Key]
        public string Path { get; set; }
        
        [Required]
        public string Hash { get; set; }
        [Required]
        public EntryType Type { get; set; }
        [Required]
        public int Size { get; set; }
        [Required]
        public DateTime AddedOn { get; set; }

        [Required]
        public Batch Batch { get; set; }
    }

    public enum EntryType
    {
        Undefined = 0,
        Directory = 1,
        Generic = 2,
        GeoImage = 3,
        GeoRaster = 4,
        PointCloud = 5,
        Image = 6,
        DroneDb = 7
    }
}
