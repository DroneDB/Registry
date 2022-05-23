using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Registry.Ports.DroneDB.Models;
using Registry.Web.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports
{
    public interface IObjectsManager
    {
        Task<IEnumerable<EntryDto>> List(string orgSlug, string dsSlug, string path = null, bool recursive = false, EntryType? type = null);
        Task<IEnumerable<EntryDto>> Search(string orgSlug, string dsSlug, string query = null, string path = null,
            bool recursive = true, EntryType? type = null);
        Task<StorageEntryDto> Get(string orgSlug, string dsSlug, string path);
        Task<EntryDto> AddNew(string orgSlug, string dsSlug, string path, byte[] data);
        Task<EntryDto> AddNew(string orgSlug, string dsSlug, string path, Stream stream = null);
        Task Move(string orgSlug, string dsSlug, string source, string dest);
        Task Delete(string orgSlug, string dsSlug, string path);
        Task DeleteAll(string orgSlug, string dsSlug);
        Task<FileStreamDescriptor> DownloadStream(string orgSlug, string dsSlug, string[] paths);
        Task<StorageFileDto> GenerateThumbnail(string orgSlug, string dsSlug, string path, int? size, bool recreate = false);
        Task<StorageFileDto> GenerateTile(string orgSlug, string dsSlug, string path, int tz, int tx, int ty, bool retina);
        Task<FileStreamDescriptor> GetDdb(string orgSlug, string dsSlug);
        Task Build(string orgSlug, string dsSlug, string path, bool background = false, bool force = false);
        Task<string> GetBuildFile(string orgSlug, string dsSlug, string hash, string path);
        Task<bool> CheckBuildFile(string orgSlug, string dsSlug, string hash, string path);
        Task<EntryType?> GetEntryType(string orgSlug, string dsSlug, string path);
    }
}
