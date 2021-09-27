using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Registry.Common;
using Registry.Common.Model;
using Registry.Ports.ObjectSystem;
using Registry.Ports.ObjectSystem.Model;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
{
    public class StorageLimitedObjectSystem : IObjectSystem
    {
        private readonly IObjectSystem _objectSystem;
        private readonly IAuthManager _authManager;
        private readonly IDdbManager _ddbManager;
        private readonly RegistryContext _context;

        public StorageLimitedObjectSystem(IObjectSystem objectSystem, IAuthManager authManager, IDdbManager ddbManager, RegistryContext context)
        {
            _objectSystem = objectSystem;
            _authManager = authManager;
            _ddbManager = ddbManager;
            _context = context;
        }
        
        public async Task PutObjectAsync(string bucketName, string objectName, Stream data, long size, string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {
            await EnforceStorageLimit(size);

            await _objectSystem.PutObjectAsync(bucketName, objectName, data, size, contentType, metaData, sse, cancellationToken);
        }

        private async Task EnforceStorageLimit(long size)
        {
            // Admins have no limits
            //if (await _authManager.IsUserAdmin()) return;

            var user = await _authManager.GetCurrentUser();
            var maxStorageObj = user?.Metadata.SafeGetValue(MagicStrings.MaxStorageKey);

            if (maxStorageObj is long maxStorage)
            {
                var userStorage = GetUserStorage(user);

                // maxStorage is in megabytes while userStorage and size are in bytes
                if (userStorage + size > maxStorage * 1024 * 1024)
                    throw new MaxUserStorageException(userStorage, maxStorage);
            }
        }

        private long GetUserStorage(User user)
        {
            // List of all user datasets
            var datasets = 
                (from org in _context.Organizations
                where org.OwnerId == user.Id 
                select org).SelectMany(org => org.Datasets).ToArray();

            long size = 0;

            foreach (var ds in datasets)
            {
                var ddb = _ddbManager.Get(ds.Organization.Slug, ds.InternalRef);

                size += ddb.GetSize();
            }

            return size;

        }

        public async Task RemoveObjectAsync(string bucketName, string objectName, CancellationToken cancellationToken = default)
        {
            await _objectSystem.RemoveObjectAsync(bucketName, objectName, cancellationToken);
        }

        public async Task RemoveObjectsAsync(string bucketName, string[] objectsNames, CancellationToken cancellationToken = default)
        {
            await _objectSystem.RemoveObjectsAsync(bucketName, objectsNames, cancellationToken);
        }


        public async Task PutObjectAsync(string bucketName, string objectName, string filePath, string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {
            await _objectSystem.PutObjectAsync(bucketName, objectName, filePath, contentType, metaData, sse, cancellationToken);
        }


        public async Task RemoveBucketAsync(string bucketName, bool force = true, CancellationToken cancellationToken = default)
        {
            await _objectSystem.RemoveBucketAsync(bucketName, force, cancellationToken);
        }

        #region Proxied

        public async Task GetObjectAsync(string bucketName, string objectName, Action<Stream> callback, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            await _objectSystem.GetObjectAsync(bucketName, objectName, callback, sse, cancellationToken);
        }

        public async Task GetObjectAsync(string bucketName, string objectName, long offset, long length, Action<Stream> cb,
            IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {
            await _objectSystem.GetObjectAsync(bucketName, objectName, offset, length, cb, sse, cancellationToken);
        }

        public async Task<ObjectInfo> GetObjectInfoAsync(string bucketName, string objectName, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            return await _objectSystem.GetObjectInfoAsync(bucketName, objectName, sse, cancellationToken);
        }

        public async Task<bool> ObjectExistsAsync(string bucketName, string objectName, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            return await _objectSystem.ObjectExistsAsync(bucketName, objectName, sse, cancellationToken);
        }

        public IObservable<ObjectUpload> ListIncompleteUploads(string bucketName, string prefix = "", bool recursive = false,
            CancellationToken cancellationToken = default)
        {
            return _objectSystem.ListIncompleteUploads(bucketName, prefix, recursive, cancellationToken);
        }

        public async Task RemoveIncompleteUploadAsync(string bucketName, string objectName, CancellationToken cancellationToken = default)
        {
            await _objectSystem.RemoveIncompleteUploadAsync(bucketName, objectName, cancellationToken);
        }

        public async Task CopyObjectAsync(string bucketName, string objectName, string destBucketName, string destObjectName = null,
            CopyConditions copyConditions = null, Dictionary<string, string> metadata = null, IServerEncryption sseSrc = null,
            IServerEncryption sseDest = null, CancellationToken cancellationToken = default)
        {
            await _objectSystem.CopyObjectAsync(bucketName, objectName, destBucketName, destObjectName, copyConditions,
                metadata, sseSrc, sseDest, cancellationToken);
        }

        public async Task GetObjectAsync(string bucketName, string objectName, string filePath, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            await EnforceStorageLimit(new FileInfo(filePath).Length);

            await _objectSystem.GetObjectAsync(bucketName, objectName, filePath, sse, cancellationToken);
        }

        public async Task MakeBucketAsync(string bucketName, string location = null, CancellationToken cancellationToken = default)
        {
            await _objectSystem.MakeBucketAsync(bucketName, location, cancellationToken);
        }

        public async Task<ListBucketsResult> ListBucketsAsync(CancellationToken cancellationToken = default)
        {
            return await _objectSystem.ListBucketsAsync(cancellationToken);
        }

        public async Task<bool> BucketExistsAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            return await _objectSystem.BucketExistsAsync(bucketName, cancellationToken);
        }

        public IObservable<ItemInfo> ListObjectsAsync(string bucketName, string prefix = null, bool recursive = false,
            CancellationToken cancellationToken = default)
        {
            return _objectSystem.ListObjectsAsync(bucketName, prefix, recursive, cancellationToken);
        }

        public async Task<string> GetPolicyAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            return await _objectSystem.GetPolicyAsync(bucketName, cancellationToken);
        }

        public async Task SetPolicyAsync(string bucketName, string policyJson, CancellationToken cancellationToken = default)
        {
            await _objectSystem.SetPolicyAsync(bucketName, policyJson, cancellationToken);
        }

        public StorageInfo GetStorageInfo()
        {
            return _objectSystem.GetStorageInfo();
        }


        public void Cleanup()
        {
            _objectSystem.Cleanup();
        }

        #endregion


    }
}
