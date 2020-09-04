using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Models.DTO
{
    public class ShareInitDto
    {
        public string Tag { get; set; }

        public string Password { get; set; }

        public override string ToString()
        {
            return Tag;
        }
    }
}
