using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
{
    public class SystemManager : ISystemManager
    {
        private readonly IChunkedUploadManager _chunkedUploadManager;
        private readonly IAuthManager _authManager;

        public SystemManager(IChunkedUploadManager chunkedUploadManager, IAuthManager authManager)
        {
            _chunkedUploadManager = chunkedUploadManager;
            _authManager = authManager;
        }

        public async Task<CleanupResult> CleanupSessions()
        {

            if (!await _authManager.IsUserAdmin())
                throw new UnauthorizedException("Only admins can perform system related tasks");
            
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
