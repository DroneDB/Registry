using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Registry.Adapters.ObjectSystem.Model;
using Registry.Common;
using Registry.Common.Model;
using Registry.Ports.ObjectSystem;
using Registry.Ports.ObjectSystem.Model;

namespace Registry.Adapters.ObjectSystem
{
    public class CachedS3ObjectSystem : IObjectSystem
    {
        private readonly PhysicalObjectSystem _localStorage;
        private readonly S3ObjectSystem _remoteStorage;

        public string CachePath { get; }
        public TimeSpan CacheExpiration { get; }
        public long MaxSize { get; }

        public CachedS3ObjectSystem(
            S3ObjectSystemSettings settings, string cachePath, TimeSpan cacheExpiration, long maxSize = 0)

        {
            CachePath = cachePath;
            CacheExpiration = cacheExpiration;
            MaxSize = maxSize;

            _remoteStorage = new S3ObjectSystem(settings);
            _localStorage = new PhysicalObjectSystem(CachePath, false);
        }

        public async Task GetObjectAsync(string bucketName, string objectName, Action<Stream> callback, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {

            try
            {
                await _localStorage.GetObjectAsync(bucketName, objectName, callback, sse, cancellationToken);
            }
            catch (Exception ex)
            {

                if (!await _localStorage.BucketExistsAsync(bucketName, cancellationToken))
                    await _localStorage.MakeBucketAsync(bucketName, null, cancellationToken);

                await _remoteStorage.GetObjectAsync(bucketName, objectName, async stream =>
                {
                    stream.Reset();

                    await _localStorage.PutObjectAsync(bucketName, objectName, stream, stream.Length, null, null, sse,
                        cancellationToken);

                    stream.Reset();

                    await _remoteStorage.GetObjectAsync(bucketName, objectName, callback, sse, cancellationToken);

                }, sse, cancellationToken);
            }

        }

        public async Task GetObjectAsync(string bucketName, string objectName, long offset, long length, Action<Stream> cb,
            IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {
            
            try
            {
                await _localStorage.GetObjectAsync(bucketName, objectName, offset, length, cb, sse, cancellationToken);
            }
            catch (Exception ex)
            {
                await _remoteStorage.GetObjectAsync(bucketName, objectName, offset, length, cb, sse, cancellationToken);
            }

        }


        public async Task GetObjectAsync(string bucketName, string objectName, string filePath, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {

            try
            {
                await _localStorage.GetObjectAsync(bucketName, objectName, filePath, sse, cancellationToken);
            }
            catch (Exception ex)
            {
                if (!await _localStorage.BucketExistsAsync(bucketName, cancellationToken))
                    await _localStorage.MakeBucketAsync(bucketName, null, cancellationToken);

                await _remoteStorage.PutObjectAsync(bucketName, objectName, filePath, null, null, sse, cancellationToken);
                await _remoteStorage.GetObjectAsync(bucketName, objectName, filePath, sse, cancellationToken);
            }
        }


        public async Task RemoveObjectAsync(string bucketName, string objectName, CancellationToken cancellationToken = default)
        {
            await Task.WhenAll(
                _remoteStorage.RemoveObjectAsync(bucketName, objectName, cancellationToken),
                _localStorage.RemoveObjectAsync(bucketName, objectName, cancellationToken));
        }

        public async Task<ObjectInfo> GetObjectInfoAsync(string bucketName, string objectName, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {

            try
            {
                return await _localStorage.GetObjectInfoAsync(bucketName, objectName, sse, cancellationToken);
            }
            catch (Exception ex)
            {
                return await _remoteStorage.GetObjectInfoAsync(bucketName, objectName, sse, cancellationToken);
            }

        }

        public IObservable<ObjectUpload> ListIncompleteUploads(string bucketName, string prefix = "", bool recursive = false,
            CancellationToken cancellationToken = default)
        {
            return _remoteStorage.ListIncompleteUploads(bucketName, prefix, recursive, cancellationToken);
        }

        public async Task RemoveIncompleteUploadAsync(string bucketName, string objectName, CancellationToken cancellationToken = default)
        {
            await _remoteStorage.RemoveIncompleteUploadAsync(bucketName, objectName, cancellationToken);
        }

        public async Task CopyObjectAsync(string bucketName, string objectName, string destBucketName, string destObjectName = null,
            IReadOnlyDictionary<string, string> copyConditions = null, Dictionary<string, string> metadata = null, IServerEncryption sseSrc = null,
            IServerEncryption sseDest = null, CancellationToken cancellationToken = default)
        {
            await _remoteStorage.CopyObjectAsync(bucketName, objectName, destBucketName, destObjectName, copyConditions, metadata, sseSrc, sseDest, cancellationToken);
        }

        #region Put

        public async Task PutObjectAsync(string bucketName, string objectName, Stream data, long size, string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {

            if (!await _localStorage.BucketExistsAsync(bucketName, cancellationToken))
                await _localStorage.MakeBucketAsync(bucketName, null, cancellationToken);

            await Task.WhenAll(
                _remoteStorage.PutObjectAsync(bucketName, objectName, data, size, contentType, metaData, sse, cancellationToken),
                _localStorage.PutObjectAsync(bucketName, objectName, data, size, contentType, metaData, sse, cancellationToken)
            );
        }

        public async Task PutObjectAsync(string bucketName, string objectName, string filePath, string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {

            if (!await _localStorage.BucketExistsAsync(bucketName, cancellationToken))
                await _localStorage.MakeBucketAsync(bucketName, null, cancellationToken);

            await Task.WhenAll(
                _remoteStorage.PutObjectAsync(bucketName, objectName, filePath, contentType, metaData, sse, cancellationToken),
                _localStorage.PutObjectAsync(bucketName, objectName, filePath, contentType, metaData, sse, cancellationToken)
            );

        }

        #endregion

        public async Task MakeBucketAsync(string bucketName, string location = null, CancellationToken cancellationToken = default)
        {
            await Task.WhenAll(
                _remoteStorage.MakeBucketAsync(bucketName, location, cancellationToken),
                _localStorage.MakeBucketAsync(bucketName, location, cancellationToken)
            );
        }

        public async Task<ListBucketsResult> ListBucketsAsync(CancellationToken cancellationToken = default)
        {
            return await _remoteStorage.ListBucketsAsync(cancellationToken);
        }

        public async Task<bool> BucketExistsAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            return await _remoteStorage.BucketExistsAsync(bucketName, cancellationToken);
        }

        public async Task RemoveBucketAsync(string bucketName, bool force = true, CancellationToken cancellationToken = default)
        {
            if (await _localStorage.BucketExistsAsync(bucketName, cancellationToken))
                await _localStorage.RemoveBucketAsync(bucketName, force, cancellationToken);

            await _remoteStorage.RemoveBucketAsync(bucketName, force, cancellationToken);

        }

        public IObservable<ItemInfo> ListObjectsAsync(string bucketName, string prefix = null, bool recursive = false,
            CancellationToken cancellationToken = default)
        {
            return _remoteStorage.ListObjectsAsync(bucketName, prefix, recursive, cancellationToken);
        }

        public async Task<string> GetPolicyAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            return await _remoteStorage.GetPolicyAsync(bucketName, cancellationToken);
        }

        public async Task SetPolicyAsync(string bucketName, string policyJson,
            CancellationToken cancellationToken = default)
        {
            await _remoteStorage.SetPolicyAsync(bucketName, policyJson, cancellationToken);
        }

        public StorageInfo GetStorageInfo()
        {
            return _remoteStorage.GetStorageInfo();
        }
    }
}
