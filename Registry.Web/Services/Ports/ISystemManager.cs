using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports
{
    public interface ISystemManager
    {
        public Task<CleanupBatchesResultDto> CleanupBatches();
        Task<CleanupDatasetResultDto> CleanupEmptyDatasets();
        string GetVersion();

        Task<IEnumerable<MigrateVisibilityEntryDTO>> MigrateVisibility();
    }
}
