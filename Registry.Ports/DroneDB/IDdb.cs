using System;
using System.Collections.Generic;
using System.Text;
using Registry.Ports.DroneDB.Model;

namespace Registry.Ports.DroneDB
{
    /// <summary>
    /// Abstracts the drone db operations
    /// </summary>
    public interface IDdb : IDisposable
    {
        public DdbObject GetObjectInfo(int id);
        IEnumerable<DdbObject> Search(string path);
    }
}
