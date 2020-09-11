using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Data.Models;

namespace Registry.Web.Models.DTO
{
    public class UploadResultDto
    {
        public string Path { get; set; }
        public string Hash { get; set; }
        public long Size { get; set; }
        
    }

    public class CommitResultDto
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public long TotalSize { get; set; }
        public int ObjectsCount { get; set; }
        public BatchStatus Status { get; set; }
    }
}
