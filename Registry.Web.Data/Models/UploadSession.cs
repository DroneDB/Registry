using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Data.Models
{
    public class UploadSession
    {
        [Key]
        public int Id { get; set; }
        public DateTime StartedOn { get; set; }
        public DateTime? EndedOn { get; set; }
        public int ChunksCount { get; set; }
        public long TotalSize { get; set; }
        public string FileName { get; set; }

        public List<FileChunk> Chunks { get; set; }
    }
}