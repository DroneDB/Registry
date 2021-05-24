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
        /// <summary>
        /// DroneDB client version
        /// </summary>
        string Version { get; }

        // This could lead to problems if we plan to move ddbpath to S3 but it's good for now
        /// <summary>
        /// DroneDB database folder
        /// </summary>
        string DatabaseFolder { get; }

        IEnumerable<DdbEntry> Search(string path, bool recursive = false);
        void Add(string path, byte[] data);
        void Add(string path, Stream data = null);
        void Remove(string path);
        void Move(string source, string dest);
        Dictionary<string, object> ChangeAttributesRaw(Dictionary<string, object> attributes);
        void GenerateThumbnail(string imagePath, int size, string outputPath);
        string GenerateTile(string imagePath, int tz, int tx, int ty, bool retina, bool tms);
        
        void Init();
        
        Dictionary<string, object> GetAttributesRaw();

        DdbAttributes GetAttributes();

        DdbEntry GetInfo();
    }
}
