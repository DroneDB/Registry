using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Ports
{
    public interface ICacheManager
    {
        public Task GenerateThumbnail(IDdb ddb, string sourcePath, string sourceHash, int size, string destPath, Func<Task> getData);
    }
}
