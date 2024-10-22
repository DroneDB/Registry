using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

public interface IShareManager
{
    public Task<ShareInitResultDto> Initialize(ShareInitDto parameters);
    public Task<UploadResultDto> Upload(string token, string path, byte[] data);
    public Task<UploadResultDto> Upload(string token, string path, Stream stream);
    public Task<CommitResultDto> Commit(string token);
    public Task Rollback(string token);
    Task<IEnumerable<BatchDto>> ListBatches(string orgSlug, string dsSlug);
    public Task<bool> IsPathAllowed(string token, string path);
    public Task<IsBatchReadyResult> IsBatchReady(string token);

    public Task<BatchDto> GetBatchInfo(string token);
}