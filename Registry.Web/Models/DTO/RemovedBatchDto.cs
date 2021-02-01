using System;
using Registry.Web.Data.Models;

namespace Registry.Web.Models.DTO
{

    public class CleanupBatchesResultDto
    {
        public RemovedBatchDto[] RemovedBatches { get; set; }
        
        public RemoveBatchErrorDto[] RemoveBatchErrors { get; set; }
    }

    public class CleanupDatasetResultDto
    {
        public string[] RemovedDatasets { get; set; }

        public CleanupDatasetErrorDto[] RemoveDatasetErrors { get; set; }

    }

    public class CleanupDatasetErrorDto
    {
        public string Dataset { get; set; }
        public string Organization { get; set; }
        public string Message { get; set; }
    }

    public class RemoveBatchErrorDto
    {
        public string Token { get; set; }
        public string Organization { get; set; }
        public string Dataset { get; set; }

        public string Message { get; set; }
    }
    public class RemovedBatchDto
    {
        public string Token { get; set; }
        public string UserName { get; set; }
        public BatchStatus Status { get; set; }
        public DateTime Start { get; set; }
        public DateTime? End { get; set; }

        public string Organization { get; set; }
        public string Dataset { get; set; }

    }
}