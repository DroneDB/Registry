using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Registry.Web.Models.DTO
{
    public class FileDescriptorStreamDto
    {
        public string Name { get; set; }
        public Stream ContentStream { get; set; }
        public string ContentType { get; set; }
    }
}
