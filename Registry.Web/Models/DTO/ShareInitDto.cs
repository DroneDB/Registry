using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Services.Ports;

namespace Registry.Web.Models.DTO
{
    public class ShareInitDto
    {
        public string Tag { get; set; }

        public string DatasetName { get; set; }
    }
}
