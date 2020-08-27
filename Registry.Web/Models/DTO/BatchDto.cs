using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Common;
using Registry.Web.Data.Models;

namespace Registry.Web.Models.DTO
{
    public class BatchDto
    {
        public string Token { get; set; }
        public string UserName { get; set; }
        public DateTime Start { get; set; }
        public DateTime? End { get; set; }

        public IEnumerable<EntryDto> Entries { get; set; }
    }

    public class EntryDto
    {
        public string Path { get; set; }
        public string Hash { get; set; }
        public EntryType Type { get; set; }
        public int Size { get; set; }
        public DateTime AddedOn { get; set; }
    }
}
