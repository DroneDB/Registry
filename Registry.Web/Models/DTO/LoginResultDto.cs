using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Models.DTO;

public class LoginResultDto
{
    public string UserName { get; set; }
    public bool Success { get; set; }
    public Dictionary<string, object> Metadata { get; set; }
}