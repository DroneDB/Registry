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
        public Task<CleanupResult> CleanupSessions();
        public Task SyncDdbMeta(string[] orgs = null, bool skipAuthCheck = false);

        // This is a hard reference and a violation of our architecture. Keeping it simple by now.
        public SyncFilesResDto SyncFiles();
    }
}
