using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB.Models;

namespace Registry.Ports.DroneDB
{
    /// <summary>
    /// Abstracts the drone db operations
    /// </summary>
    public interface IDdb
    {

        /// <summary>
        /// Name of the database folder (example: .ddb)
        /// </summary>
        string DatabaseFolderName { get; }
        
        /// <summary>
        /// Name of the build folder (example: build)
        /// </summary>
        string BuildFolderName { get; }
        
        /// <summary>
        /// DroneDB client version
        /// </summary>
        string Version { get; }

        /// <summary>
        /// DroneDB dataset folder path
        /// </summary>
        string DatasetFolderPath { get; }

        /// <summary>
        /// The build folder path
        /// </summary>
        string BuildFolderPath { get; }

        IEnumerable<DdbEntry> Search(string path, bool recursive = false);
        void Add(string path, byte[] data);
        void Add(string path, Stream data = null);

        void Remove(string path);
        void Move(string source, string dest);

        Dictionary<string, object> ChangeAttributesRaw(Dictionary<string, object> attributes);
        byte[] GenerateThumbnail(string imagePath, int size);
        byte[] GenerateTile(string inputPath, int tz, int tx, int ty, bool retina, string inputPathHash);

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

        IDdbMetaManager Meta { get;  }
        long GetSize();

        #region Async

        Task<IEnumerable<DdbEntry>> SearchAsync(string path, bool recursive = false, CancellationToken cancellationToken = default);
        Task AddAsync(string path, byte[] data, CancellationToken cancellationToken = default);
        Task AddAsync(string path, Stream data = null, CancellationToken cancellationToken = default);
        Task RemoveAsync(string path, CancellationToken cancellationToken = default);
        Task MoveAsync(string source, string dest, CancellationToken cancellationToken = default);
        Task<Dictionary<string, object>> ChangeAttributesRawAsync(Dictionary<string, object> attributes, CancellationToken cancellationToken = default);
        Task<byte[]> GenerateThumbnailAsync(string imagePath, int size, CancellationToken cancellationToken = default);
        Task<byte []> GenerateTileAsync(string inputPath, int tz, int tx, int ty, bool retina, string inputPathHash, CancellationToken cancellationToken = default);
        Task InitAsync(CancellationToken cancellationToken = default);
        Task<Dictionary<string, object>> GetAttributesRawAsync(CancellationToken cancellationToken = default);
        Task<DdbAttributes> GetAttributesAsync(CancellationToken cancellationToken = default);
        Task<DdbEntry> GetInfoAsync(CancellationToken cancellationToken = default);
        Task<DdbEntry> GetInfoAsync(string path, CancellationToken cancellationToken = default);
        Task<DdbEntry> GetEntryAsync(string path, CancellationToken cancellationToken = default);
        Task<bool> EntryExistsAsync(string path, CancellationToken cancellationToken = default);
        Task BuildAsync(string path, string dest = null, bool force = false, CancellationToken cancellationToken = default);
        Task BuildAllAsync(string dest = null, bool force = false, CancellationToken cancellationToken = default);
        Task<bool> IsBuildableAsync(string path, CancellationToken cancellationToken = default);
        Task<long> GetSizeAsync(CancellationToken cancellationToken = default);

        #endregion

    }
}
