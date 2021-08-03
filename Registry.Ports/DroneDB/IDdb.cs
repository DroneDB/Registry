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

        string BuildFolder { get; }

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

        /// <summary>
        /// Calls DDB info command on specified path
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        DdbEntry GetInfo(string path);

        /// <summary>
        /// Gets the specified path inside the DDB database
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        DdbEntry GetEntry(string path);

        bool EntryExists(string path);
        void Build(string path, string dest = null, bool force = false);
        void BuildAll(string dest = null, bool force = false);

        bool IsBuildable(string path);
    }
}
