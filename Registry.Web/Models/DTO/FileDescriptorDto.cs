using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Models.DTO
{
    public class FileDescriptorDto
    {
        public string Name { get; set; }
        public Stream ContentStream { get; set; }
        public string ContentType { get; set; }
    }
}
