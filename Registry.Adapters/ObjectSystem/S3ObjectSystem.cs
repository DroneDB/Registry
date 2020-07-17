using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Minio;
using Minio.DataModel;
using Registry.Ports.FileSystem.Model;
using Registry.Ports.ObjectSystem;
using Registry.Ports.ObjectSystem.Model;

namespace Registry.Adapters.ObjectSystem
{
    public class S3ObjectSystem : IObjectSystem
    {

        private readonly MinioClient _client;

        public S3ObjectSystem(string endpoint, string accessKey = "", string secretKey = "", string region = "",
            string sessionToken = "", bool useSsl = false, string appName = "", string appVersion = "")
        {
            _client = new MinioClient(endpoint, accessKey, secretKey, region, sessionToken);

            if (useSsl)
                _client.WithSSL();

            _client.SetAppInfo(appName, appVersion);

        }

        public async Task GetObjectAsync(string bucketName, string objectName, Action<Stream> callback,
            IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {
            await _client.GetObjectAsync(bucketName, objectName, callback, sse.ToSSE(), cancellationToken);
        }

        public async Task GetObjectAsync(string bucketName, string objectName, long offset, long length, Action<Stream> cb,
            IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {
            await _client.GetObjectAsync(bucketName, objectName, cb, sse?.ToSSE(), cancellationToken);
        }

        public async Task PutObjectAsync(string bucketName, string objectName, Stream data, long size, string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {
            await _client.PutObjectAsync(bucketName, objectName, data, size, contentType, metaData, sse?.ToSSE(),
                cancellationToken);
        }

        public async Task RemoveObjectAsync(string bucketName, string objectName, CancellationToken cancellationToken = default)
        {
            await _client.RemoveObjectAsync(bucketName, objectName, cancellationToken);
        }

        public async Task<ObjectInfo> GetObjectInfoAsync(string bucketName, string objectName, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            var res = await _client.StatObjectAsync(bucketName, objectName, sse?.ToSSE(), cancellationToken);

            return new ObjectInfo(res.ObjectName, res.Size, res.LastModified, res.ETag, res.ContentType, res.MetaData);
        }

        public IObservable<ObjectUpload> ListIncompleteUploads(string bucketName, string prefix = "", bool recursive = false,
            CancellationToken cancellationToken = default)
        {

            var res = _client.ListIncompleteUploads(bucketName, prefix, recursive, cancellationToken);

            return res.Select(item => new ObjectUpload
            {
                Initiated = item.Initiated,
                Key = item.Key,
                UploadId = item.UploadId
            });
            
        }

        public async Task RemoveIncompleteUploadAsync(string bucketName, string objectName, CancellationToken cancellationToken = default)
        {
            await _client.RemoveIncompleteUploadAsync(bucketName, objectName, cancellationToken);
        }

        public async Task CopyObjectAsync(string bucketName, string objectName, string destBucketName, string destObjectName = null,
            IReadOnlyDictionary<string, string> copyConditions = null, Dictionary<string, string> metadata = null, IServerEncryption sseSrc = null,
            IServerEncryption sseDest = null, CancellationToken cancellationToken = default)
        {
            // TODO: Find out how to implement CopyCondition
            throw new NotImplementedException();
        }

        public async Task PutObjectAsync(string bucketName, string objectName, string filePath, string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {
            await _client.PutObjectAsync(bucketName, objectName, filePath, contentType, metaData, sse?.ToSSE(),
                cancellationToken);
        }

        public async Task GetObjectAsync(string bucketName, string objectName, string filePath, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            await _client.GetObjectAsync(bucketName, objectName, filePath, sse?.ToSSE(), cancellationToken);
        }

        public async Task MakeBucketAsync(string bucketName, string location, CancellationToken cancellationToken = default)
        {
            await _client.MakeBucketAsync(bucketName, location, cancellationToken);
        }

        public async Task<ListBucketsResult> ListBucketsAsync(CancellationToken cancellationToken = default)
        {
            var res = await _client.ListBucketsAsync(cancellationToken);

            return new ListBucketsResult
            {
                Owner = res.Owner,
                Buckets = res.Buckets.Select(item => new BucketInfo
                {
                    CreationDate = item.CreationDateDateTime,
                    Name = item.Name
                })
            };
        }

        public async Task<bool> BucketExistsAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            return await _client.BucketExistsAsync(bucketName, cancellationToken);
        }

        public async Task RemoveBucketAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            await _client.RemoveBucketAsync(bucketName, cancellationToken);
        }

        public IObservable<ItemInfo> ListObjectsAsync(string bucketName, string prefix = null, bool recursive = false,
            CancellationToken cancellationToken = default)
        {
            return _client.ListObjectsAsync(bucketName, prefix, recursive, cancellationToken)
                .Select(item => new ItemInfo
                {
                    IsDir = item.IsDir,
                    Key = item.Key,
                    LastModified = item.LastModifiedDateTime,
                    Size = item.Size
                });
        }

        public async Task<string> GetPolicyAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            return await _client.GetPolicyAsync(bucketName, cancellationToken);
        }

        public async Task SetPolicyAsync(string bucketName, string policyJson, CancellationToken cancellationToken = default)
        {
            await _client.SetPolicyAsync(bucketName, policyJson, cancellationToken);
        }

    }
}
