using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Ports
{
    public interface ICacheManager
    {
        public Task<byte []> GenerateThumbnail(IDdb ddb, string sourcePath, string sourceHash, int size);

        public Task<byte[]> GenerateTile(IDdb ddb, string sourcePath, string sourceHash, int tz, int tx, int ty,
            bool retina);

        public Task ClearThumbnails(string sourceHash);
        public Task ClearTiles(string sourceHash);

    }
}
