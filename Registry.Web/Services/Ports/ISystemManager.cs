using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Adapters.ObjectSystem;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports
{
    public interface ISystemManager
    {
        public SyncFilesResDto SyncFiles();
        public Task<CleanupBatchesResultDto> CleanupBatches();
        Task<CleanupDatasetResultDto> CleanupEmptyDatasets();
        string GetVersion();
    }
}
