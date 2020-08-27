using System;
using System.Collections.Generic;
using System.Text;
using Registry.Ports.DroneDB.Models;

namespace Registry.Ports.DroneDB
{
    /// <summary>
    /// Abstracts the drone db operations
    /// </summary>
    public interface IDdbStorage : IDisposable
    {
        // public DdbObject GetObjectInfo(int id);
        IEnumerable<DdbEntry> Search(string path);
        void Add(string path, byte[] data);
        void Remove(string path);
    }
}
