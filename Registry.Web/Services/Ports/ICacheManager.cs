using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Ports
{
    public interface ICacheManager
    {
        public void GenerateThumbnail(IDdb ddb, string sourcePath, int size, string destPath, Func<Task> getData);
    }
}
