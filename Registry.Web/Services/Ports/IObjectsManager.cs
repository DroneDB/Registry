using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient.DataClassification;
using Registry.Web.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports
{
    public interface IObjectsManager
    {
        Task<IEnumerable<ObjectDto>> List(string orgSlug, string dsSlug, string path);
        Task<ObjectRes> Get(string orgSlug, string dsSlug, string path);
        Task<UploadedObjectDto> AddNew(string orgSlug, string dsSlug, string path, byte[] data);
        Task<UploadedObjectDto> AddNew(string orgSlug, string dsSlug, string path, Stream stream);
        Task Delete(string orgSlug, string dsSlug, string path);

        Task DeleteAll(string orgSlug, string dsSlug);

        Task<int> AddNewSession(string orgSlug, string dsSlug, int chunks, long size);
        Task AddToSession(string orgSlug, string dsSlug, int sessionId, int index, Stream stream);
        Task AddToSession(string orgSlug, string dsSlug, int sessionId, int index, byte[] data);
        Task<UploadedObjectDto> CloseSession(string orgSlug, string dsSlug, int sessionId, string path);
    }
}
