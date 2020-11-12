using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Models
{
    public class JwtDescriptor
    {
        public string Token { get; set; }
        public DateTime ExpiresOn { get; set; }
    }
}
