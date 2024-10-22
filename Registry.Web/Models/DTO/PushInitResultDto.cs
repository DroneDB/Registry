using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Registry.Web.Models.DTO;

public class PushInitResultDto
{
    public string[] NeededFiles { get; set; }
    public string[] NeededMeta { get; set; }
    public bool PullRequired { get; set; }

    public string Token { get; set; }
}