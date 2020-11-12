using System;
using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Data.Models
{
    public class FileChunk
    {
        [Key]
        public int Id { get; set; }

        public int Index { get; set; }
        public DateTime Date { get; set; }
        public long Size { get; set; }
        public UploadSession Session { get; set; }
    }
}