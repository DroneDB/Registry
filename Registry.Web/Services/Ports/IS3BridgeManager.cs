using System;
using System.IO;
using System.Threading.Tasks;

namespace Registry.Web.Services.Ports
{
    public interface IS3BridgeManager
    {
        Task<bool> ObjectExists(string bucketName, string path);
        Task GetObjectStream(string bucketName, string objectName, long offset, long length, Action<Stream> cb);
        Task<string> GetObject(string bucketName, string objectName);
        Task RemoveObjectFromCache(string bucketName, string objectName);

        bool IsS3Based();
    }
}