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
        public string Url { get; set; }

        public TagDto Tag { get; set; }
    }
}
