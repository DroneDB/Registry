using Registry.Adapters.DroneDB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Services.Ports
{
    /// <summary>
    /// Creates new instances of DDB
    /// </summary>
    public interface IDdbManager
    {
        IDDB Get(string orgSlug, Guid internalRef);
        void Delete(string orgSlug, Guid internalRef);
    }
}
