using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Data.Models;

namespace Registry.Web.Models.DTO
{
    public class IsBatchReadyResult
    {
        public IsBatchReadyResult(bool isReady, Batch batch)
        {
            IsReady = isReady;
            Batch = batch;
        }

        public IsBatchReadyResult(bool isReady)
        {
            IsReady = isReady;
        }

        public bool IsReady { get; }
        public Batch Batch { get; }

        public static readonly IsBatchReadyResult NotReady = new IsBatchReadyResult(false);
    }
}
