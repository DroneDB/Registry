using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Models.DTO;

public class CleanupResult
{
    public int[] RemovedSessions { get; set; }
}