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
        IDdb Get(string orgSlug, Guid internalRef);
        void Delete(string orgSlug, Guid internalRef);

        public string DdbFolderName { get; }
    }
}
