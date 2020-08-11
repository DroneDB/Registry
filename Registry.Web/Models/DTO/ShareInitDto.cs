using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Models.DTO
{
    public class ShareInitDto
    {
        public OrganizationDto Organization { get; set; }
        public DatasetDto Dataset { get; set; }
    }
}
