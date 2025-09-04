using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Models.DTO;

public class UserDetailDto
{
    public string UserName { get; set; }
    public string Email { get; set; }
    public string[] Roles { get; set; }
    public string[] Organizations { get; set; }
    public long? StorageQuota { get; set; }
    public long StorageUsed { get; set; }
    public int OrganizationCount { get; set; }
    public int DatasetCount { get; set; }
    public DateTime CreatedDate { get; set; }
}
