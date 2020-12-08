using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Ports
{
    /// <summary>
    /// Creates new instances of IDdb
    /// </summary>
    public interface IDdbManager
    {
        IDdb Get(string orgSlug, string dsSlug);
        void Move(string orgSlug, string dsSlug, string newDsSlug);
        void Delete(string orgSlug, string dsSlug);
    }
}
