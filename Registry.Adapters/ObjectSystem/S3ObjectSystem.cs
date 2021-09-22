using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Minio;
using Minio.DataModel;
using Minio.Exceptions;
using Newtonsoft.Json;
using Registry.Adapters.ObjectSystem.Model;
using Registry.Common;
using Registry.Common.Model;
using Registry.Ports.ObjectSystem;
using Registry.Ports.ObjectSystem.Model;
using CopyConditions = Registry.Ports.ObjectSystem.Model.CopyConditions;

namespace Registry.Adapters.ObjectSystem
{
    public class S3ObjectSystem : IObjectSystem
    {
        [JsonProperty]
        private readonly S3ObjectSystemSettings _settings;

        private readonly MinioClient _client;

        public S3ObjectSystem(S3ObjectSystemSettings settings)
        {
            _settings = settings;
            _client = new MinioClient(settings.Endpoint, settings.AccessKey ?? string.Empty, settings.SecretKey ?? string.Empty, 
                settings.Region ?? string.Empty, settings.SessionToken ?? string.Empty);

            if (settings.UseSsl)
                _client.WithSSL();

            if (!string.IsNullOrWhiteSpace(settings.AppName) && !string.IsNullOrWhiteSpace(settings.AppVersion))
                _client.SetAppInfo(settings.AppName, settings.AppVersion);

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
            data.Reset();

            await _client.PutObjectAsync(bucketName, objectName, data, size, contentType, metaData, sse?.ToSSE(),
                cancellationToken);
        }

        public async Task RemoveObjectAsync(string bucketName, string objectName, CancellationToken cancellationToken = default)
        {
            await _client.RemoveObjectAsync(bucketName, objectName, cancellationToken);
        }

        public async Task RemoveObjectsAsync(string bucketName, string[] objectsNames, CancellationToken cancellationToken = default)
        {
            var res = (await _client.RemoveObjectAsync(bucketName, objectsNames, cancellationToken)).ToEnumerable().ToArray();
        }

        public async Task<ObjectInfo> GetObjectInfoAsync(string bucketName, string objectName, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            var res = await _client.StatObjectAsync(bucketName, objectName, sse?.ToSSE(), cancellationToken);

            return new ObjectInfo(res.ObjectName, res.Size, res.LastModified, res.ETag, res.ContentType, res.MetaData);
        }

        public async Task<bool> ObjectExistsAsync(string bucketName, string objectName, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await _client.StatObjectAsync(bucketName, objectName, sse?.ToSSE(), cancellationToken);

                return true;
            }
            catch (MinioException e)
            {
                Debug.WriteLine($"Object '{objectName}' in bucket '{bucketName}' does not exist: {e}");
            }

            return false;

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
            CopyConditions copyConditions = null, Dictionary<string, string> metadata = null, IServerEncryption sseSrc = null,
            IServerEncryption sseDest = null, CancellationToken cancellationToken = default)
        {

            await _client.CopyObjectAsync(bucketName, objectName, destBucketName, destObjectName, copyConditions?.ToS3CopyConditions(), metadata,
                sseSrc.ToSSE(), sseDest.ToSSE(), cancellationToken);
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

        public async Task MakeBucketAsync(string bucketName, string location = null, CancellationToken cancellationToken = default)
        {
            try
            {

                await _client.MakeBucketAsync(bucketName, location ?? "us-east-1", cancellationToken);

            }
            catch (MinioException ex)
            {
                // This ugly mess is a fast and dirty way to skip che error message thrown when we are trying to create an already existing bucket
                // The issue is due to the async nature of S3. Many times we do "If !BucketExists Then MakeBucket"
                // BUT it's not safe in a highly multithreaded context where the same code is executed in parallel.
                // It happens that two threads are executing the same If !BucketExists and then two MakeBucket -> Exception
                if (!ex.XmlError.Contains("<Code>BucketAlreadyOwnedByYou</Code>",
                    StringComparison.OrdinalIgnoreCase)) throw;

                Debug.WriteLine("Eating BucketAlreadyOwnedByYou exception");

            }
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

        public async Task RemoveBucketAsync(string bucketName, bool force = true, CancellationToken cancellationToken = default)
        {

            if (force)
            {
                var objects = _client.ListObjectsAsync(bucketName, null, true, cancellationToken).ToEnumerable().Select(obj => obj.Key).ToArray();

                foreach (var obj in objects)
                    await _client.RemoveObjectAsync(bucketName, obj, cancellationToken);
            }

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

        public StorageInfo GetStorageInfo()
        {
            // By definition S3 does not have the concept of "available space"
            return null;
        }

        public void Cleanup()
        {
            //
        }
    }
}
