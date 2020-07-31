using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Ports
{
    public interface IDdbFactory
    {
        IDdb GetDdb(string orgId, string dsId);
    }
}
