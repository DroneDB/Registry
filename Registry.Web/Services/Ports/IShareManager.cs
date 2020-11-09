using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports
{
    public interface IShareManager
    {
        public Task<ShareInitResultDto> Initialize(ShareInitDto parameters);
        public Task<UploadResultDto> Upload(string token, string path, byte[] data);
        public Task<UploadResultDto> Upload(string token, string path, Stream stream);
        public Task<CommitResultDto> Commit(string token, bool rollback = false);
        Task<IEnumerable<BatchDto>> ListBatches(string orgSlug, string dsSlug);
        public Task<bool> IsPathAllowed(string token, string path);
        public Task<IsBatchReadyResult> IsBatchReady(string token);

        public Task<int> StartUploadSession(string token, int chunks, long size);
        public Task UploadToSession(string token, int sessionId, int index, Stream stream);
        public Task UploadToSession(string token, int sessionId, int index, byte[] data);

        public Task<UploadResultDto> CloseUploadSession(string token, int sessionId, string path);

    }
}
