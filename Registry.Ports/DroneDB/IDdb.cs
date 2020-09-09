using System;
using System.Collections.Generic;
using System.Text;
using Registry.Ports.DroneDB.Models;

namespace Registry.Ports.DroneDB
{
    public interface IDdb
    {
        IEnumerable<DdbInfo> Info(string path);
        void CreateDatabase(string path);
        void Remove(string ddbPath, string path);
    }
}
