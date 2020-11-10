using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Services.Ports
{
    public interface IChunkedUploadManager
    {
        public int InitSession(string fileName, int chunks, long size);
        public void Upload(int sessionId, Stream chunkStream, int index);
        public string CloseSession(int sessionId, bool performCleanup = true);

        public void RemoveTimedoutSessions();
        public void RemoveClosedSessions();

        public void CleanupSession(int sessionId);
    }
}
