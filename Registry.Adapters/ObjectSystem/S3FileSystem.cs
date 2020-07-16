using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Minio;
using Registry.Ports.FileSystem.Model;
using Registry.Ports.ObjectSystem;
using Registry.Ports.ObjectSystem.Model;

namespace Registry.Adapters.ObjectSystem
{
    public class S3ObjectSystem : IObjectSystem
    {

        private readonly MinioClient _client;

        public S3ObjectSystem(string endpoint, string accessKey = "", string secretKey = "", string region = "",
            string sessionToken = "")
        {
            _client = new MinioClient(endpoint, accessKey, secretKey, region, sessionToken);
        }

        public async Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await _client.BucketExistsAsync(bucket, cancellationToken);
        }

        public async Task<EnumerateBucketsResult> EnumerateBucketsAsync(CancellationToken cancellationToken = default(CancellationToken))
        {

            var res = await _client.ListBucketsAsync(cancellationToken);

            return new EnumerateBucketsResult
            {
                Buckets = res.Buckets.Select(item => new BucketInfo { CreationDate = item.CreationDateDateTime, Name = item.Name })
            };

        }

        public void CreateDirectory(string bucket, string path)
        {
            throw new NotImplementedException();
        }

        public async Task DeleteObject(string bucket, string path, CancellationToken cancellationToken)
        {
            await _client.RemoveObjectAsync(bucket, path, cancellationToken);
        }

        public bool DirectoryExists(string bucket, string path)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<string> EnumerateObjects(string bucket, string path, string searchPattern, SearchOption searchOption)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<string> EnumerateFolders(string bucket, string path, string searchPattern, SearchOption searchOption)
        {
            throw new NotImplementedException();
        }

        public async Task<bool> ObjectExists(string bucket, string path, CancellationToken cancellationToken = default)
        {
            
            try {
                var res = await _client.StatObjectAsync(bucket, path, null, cancellationToken);
                return res != null;
            // Check docs: MinioException
            } catch (Exception e) {
                return false;
            }
            
        }

        public string ReadAllText(string bucket, string path, Encoding encoding)
        {
            throw new NotImplementedException();
        }

        public byte[] ReadAllBytes(string bucket, string path)
        {
            throw new NotImplementedException();
        }

        public void RemoveDirectory(string bucket, string path, bool recursive)
        {
            throw new NotImplementedException();
        }

        public void WriteAllText(string bucket, string path, string contents, Encoding encoding)
        {
            throw new NotImplementedException();
        }

        public void WriteAllBytes(string bucket, string path, byte[] contents)
        {
            throw new NotImplementedException();
        }
    }
}
