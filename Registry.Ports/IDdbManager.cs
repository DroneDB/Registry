using System;
using Registry.Ports.DroneDB;

namespace Registry.Ports
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
