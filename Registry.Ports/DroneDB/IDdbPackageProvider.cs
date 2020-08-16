using System;
using System.Collections.Generic;
using System.Text;

namespace Registry.Ports.DroneDB
{
    public interface IDdbPackageProvider
    {
        public bool IsDdbReady();
        public void DownloadDdb();
        public void EnsureDdb();
    }
}
