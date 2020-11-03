using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Registry.Ports.DroneDB.Models;

namespace Registry.Ports.DroneDB
{
    /// <summary>
    /// Abstracts the drone db operations
    /// </summary>
    public interface IDdb
    {
        // public DdbObject GetObjectInfo(int id);
        IEnumerable<DdbEntry> Search(string path);
        void Add(string path, byte[] data);
        void Add(string path, Stream data);
        void Remove(string path);
    }
}
