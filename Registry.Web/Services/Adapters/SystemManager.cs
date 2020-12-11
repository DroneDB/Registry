using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
{
    public class SystemManager : ISystemManager
    {
        private readonly IChunkedUploadManager _chunkedUploadManager;

        public SystemManager(IChunkedUploadManager chunkedUploadManager)
        {
            _chunkedUploadManager = chunkedUploadManager;
        }

        public async Task<CleanupResult> CleanupSessions()
        {
            
            var removedSessions = new List<int>();
            removedSessions.AddRange(await _chunkedUploadManager.RemoveTimedoutSessions());
            removedSessions.AddRange(await _chunkedUploadManager.RemoveClosedSessions());

            return new CleanupResult
            {
                RemovedSessions = removedSessions.ToArray()
            };

        }
    }
}
