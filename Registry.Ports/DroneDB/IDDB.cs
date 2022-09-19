using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Registry.Ports.DroneDB.Models;

namespace Registry.Ports.DroneDB
{
    public interface IDDB
    {
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

        IEnumerable<Entry> Search(string path, bool recursive = false);
        void Add(string path, byte[] data);
        void Add(string path, Stream data = null);
        void AddRaw(string path);

        void Remove(string path);
        void Move(string source, string dest);

        [Obsolete("Use meta manager instead")]
        Dictionary<string, object> ChangeAttributesRaw(Dictionary<string, object> attributes);
        
        [Obsolete("Use meta manager instead")]
        Dictionary<string, object> GetAttributesRaw();

        byte[] GenerateThumbnail(string imagePath, int size);
        byte[] GenerateTile(string inputPath, int tz, int tx, int ty, bool retina, string inputPathHash);

        void Init();

        Entry GetInfo();

        /// <summary>
        /// Calls DDB info command on specified path
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        Entry GetInfo(string path);

        string GetLocalPath(string path);

        /// <summary>
        /// Gets the specified path inside the DDB database
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        Entry GetEntry(string path);

        bool EntryExists(string path);
        void Build(string path, string dest = null, bool force = false);
        void BuildAll(string dest = null, bool force = false);
        void BuildPending(string dest = null, bool force = false);

        public string GetTmpFolder(string path);
        bool IsBuildable(string path);
        bool IsBuildPending();

        IMetaManager Meta { get; }
        long GetSize();
        Stamp GetStamp();

        JToken GetStac(string id, string stacCollectionRoot, string stacCatalogRoot, string path = null);

        #region Async

        Task<IEnumerable<Entry>> SearchAsync(string path, bool recursive = false, CancellationToken cancellationToken = default);
        Task AddAsync(string path, byte[] data, CancellationToken cancellationToken = default);
        Task AddAsync(string path, Stream data = null, CancellationToken cancellationToken = default);
        Task RemoveAsync(string path, CancellationToken cancellationToken = default);
        Task MoveAsync(string source, string dest, CancellationToken cancellationToken = default);
        
        [Obsolete("Use meta manager instead")]
        Task<Dictionary<string, object>> ChangeAttributesRawAsync(Dictionary<string, object> attributes, CancellationToken cancellationToken = default);
        Task<byte[]> GenerateThumbnailAsync(string imagePath, int size, CancellationToken cancellationToken = default);
        Task<byte[]> GenerateTileAsync(string inputPath, int tz, int tx, int ty, bool retina, string inputPathHash, CancellationToken cancellationToken = default);
        Task InitAsync(CancellationToken cancellationToken = default);
        Task<Dictionary<string, object>> GetAttributesRawAsync(CancellationToken cancellationToken = default);
        Task<Entry> GetInfoAsync(CancellationToken cancellationToken = default);
        Task<Entry> GetInfoAsync(string path, CancellationToken cancellationToken = default);
        Task<Entry> GetEntryAsync(string path, CancellationToken cancellationToken = default);
        Task<bool> EntryExistsAsync(string path, CancellationToken cancellationToken = default);
        Task BuildAsync(string path, string dest = null, bool force = false, CancellationToken cancellationToken = default);
        Task BuildAllAsync(string dest = null, bool force = false, CancellationToken cancellationToken = default);
        Task<bool> IsBuildableAsync(string path, CancellationToken cancellationToken = default);
        Task<bool> IsBuildPendingAsync(CancellationToken cancellationToken = default);

        Task<long> GetSizeAsync(CancellationToken cancellationToken = default);

        #endregion
        
        // These consts are like magic strings: if anything changes this goes kaboom!
        public const string DatabaseFolderName = ".ddb";
        public const string BuildFolderName = "build";
        public const string TmpFolderName = "tmp";
    }
}
