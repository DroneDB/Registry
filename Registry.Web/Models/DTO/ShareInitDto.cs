using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Models.DTO
{
    public class ShareInitDto
    {

        public string OrganizationSlug { get; set; }
        public string OrganizationName { get; set; }
        public string DatasetSlug { get; set; }
        public string DatasetName { get; set; }

        public string Password { get; set; }

        public override string ToString()
        {
            return $"{OrganizationSlug ?? OrganizationName}/{DatasetSlug ?? DatasetName}";
        }
    }
}
