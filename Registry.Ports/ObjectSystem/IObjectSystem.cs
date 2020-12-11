using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Registry.Common.Model;
using Registry.Ports.ObjectSystem.Model;

namespace Registry.Ports.ObjectSystem
{
    /// <summary>
    /// Object system abstraction
    /// </summary>
    public interface IObjectSystem
    {

        /// <summary>
        /// Get an object. The object will be streamed to the callback given by the user.
        /// </summary>
        /// <param name="bucketName">Bucket to retrieve object from</param>
        /// <param name="objectName">Name of object to retrieve</param>
        /// <param name="callback">A stream will be passed to the callback</param>
        /// <param name="sse">Optional Server-side encryption option. Defaults to null.</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        Task GetObjectAsync(string bucketName, string objectName, Action<Stream> callback, IServerEncryption sse = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Get an object. The object will be streamed to the callback given by the user.
        /// </summary>
        /// <param name="bucketName">Bucket to retrieve object from</param>
        /// <param name="objectName">Name of object to retrieve</param>
        /// <param name="offset">offset of the object from where stream will start </param>
        /// <param name="length">length of object to read in from the stream</param>
        /// <param name="cb">A stream will be passed to the callback</param>
        /// <param name="sse">Optional Server-side encryption option. Defaults to null.</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        Task GetObjectAsync(string bucketName, string objectName, long offset, long length, Action<Stream> cb, IServerEncryption sse = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates an object from file input stream
        /// </summary>
        /// <param name="bucketName">Bucket to create object in</param>
        /// <param name="objectName">Key of the new object</param>
        /// <param name="data">Stream of file to upload</param>
        /// <param name="size">Size of stream</param>
        /// <param name="contentType">Content type of the new object, null defaults to "application/octet-stream"</param>
        /// <param name="metaData">Optional Object metadata to be stored. Defaults to null.</param>
        /// <param name="sse">Optional Server-side encryption option. Defaults to null.</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        Task PutObjectAsync(string bucketName, string objectName, Stream data, long size, string contentType = null, Dictionary<string, string> metaData = null, IServerEncryption sse = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes an object with given name in specific bucket
        /// </summary>
        /// <param name="bucketName">Bucket to remove object from</param>
        /// <param name="objectName">Key of object to remove</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        /// <returns></returns>
        Task RemoveObjectAsync(string bucketName, string objectName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Tests the object's existence and returns metadata about existing objects.
        /// </summary>
        /// <param name="bucketName">Bucket to test object in</param>
        /// <param name="objectName">Name of the object to stat</param>
        /// <param name="sse">Optional Server-side encryption option. Defaults to null.</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        /// <returns>Facts about the object</returns>
        Task<ObjectInfo> GetObjectInfoAsync(string bucketName, string objectName, IServerEncryption sse = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists all incomplete uploads in a given bucket and prefix recursively
        /// </summary>
        /// <param name="bucketName">Bucket to list all incomplete uploads from</param>
        /// <param name="prefix">prefix to list all incomplete uploads</param>
        /// <param name="recursive">Set to true to recursively list all incomplete uploads</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        /// <returns>A lazily populated list of incomplete uploads</returns>
        IObservable<ObjectUpload> ListIncompleteUploads(string bucketName, string prefix = "", bool recursive = false, CancellationToken cancellationToken = default);

        /// <summary>
        /// Remove incomplete uploads from a given bucket and objectName
        /// </summary>
        /// <param name="bucketName">Bucket to remove incomplete uploads from</param>
        /// <param name="objectName">Key to remove incomplete uploads from</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        Task RemoveIncompleteUploadAsync(string bucketName, string objectName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Copy a source object into a new destination object.
        /// </summary>
        /// <param name="bucketName"> Bucket name where the object to be copied exists.</param>
        /// <param name="objectName">Object name source to be copied.</param>
        /// <param name="destBucketName">Bucket name where the object will be copied to.</param>
        /// <param name="destObjectName">Object name to be created, if not provided uses source object name as destination object name.</param>
        /// <param name="copyConditions">optionally can take a key value CopyConditions as well for conditionally attempting copyObject.</param>
        /// <param name="metadata">Optional Object metadata to be stored. Defaults to null.</param>
        /// <param name="sseSrc">Optional Server-side encryption option for source. Defaults to null.</param>
        /// <param name="sseDest">Optional Server-side encryption option for destination. Defaults to null.</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        /// <returns></returns>
        Task CopyObjectAsync(string bucketName, string objectName, string destBucketName, string destObjectName = null, IReadOnlyDictionary<string, string> copyConditions = null, Dictionary<string, string> metadata = null, IServerEncryption sseSrc = null, IServerEncryption sseDest = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates an object from file
        /// </summary>
        /// <param name="bucketName">Bucket to create object in</param>
        /// <param name="objectName">Key of the new object</param>
        /// <param name="filePath">Path of file to upload</param>
        /// <param name="contentType">Content type of the new object, null defaults to "application/octet-stream"</param>
        /// <param name="metaData">Optional Object metadata to be stored. Defaults to null.</param>
        /// <param name="sse">Optional Server-side encryption option. Defaults to null.</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        Task PutObjectAsync(string bucketName, string objectName, string filePath, string contentType = null, Dictionary<string, string> metaData = null, IServerEncryption sse = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Get an object. The object will be streamed to the callback given by the user.
        /// </summary>
        /// <param name="bucketName">Bucket to retrieve object from</param>
        /// <param name="objectName">Name of object to retrieve</param>
        /// <param name="filePath">string with file path</param>
        /// <param name="sse">Optional Server-side encryption option. Defaults to null.</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        /// <returns></returns>
        Task GetObjectAsync(string bucketName, string objectName, string filePath, IServerEncryption sse = null, CancellationToken cancellationToken = default);


        /// <summary>
        /// Create a private bucket with the given name.
        /// </summary>
        /// <param name="bucketName">Name of the new bucket</param>
        /// <param name="location">Region</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        /// <returns>Task</returns>
        Task MakeBucketAsync(string bucketName, string location = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// List all objects in a bucket
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        /// <returns>Task with an iterator lazily populated with objects</returns>
        Task<ListBucketsResult> ListBucketsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns true if the specified bucketName exists, otherwise returns false.
        /// </summary>
        /// <param name="bucketName">Bucket to test existence of</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        /// <returns>Task that returns true if exists and user has access</returns>
        Task<bool> BucketExistsAsync(string bucketName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Remove a bucket
        /// </summary>
        /// <param name="bucketName">Name of bucket to remove</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        /// <returns>Task</returns>
        Task RemoveBucketAsync(string bucketName, CancellationToken cancellationToken = default);

        /// <summary>
        /// List all objects non-recursively in a bucket with a given prefix, optionally emulating a directory
        /// </summary>
        /// <param name="bucketName">Bucket to list objects from</param>
        /// <param name="prefix">Filter all incomplete uploads starting with this prefix</param>
        /// <param name="recursive">List incomplete uploads recursively</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        /// <returns>An observable of items that client can subscribe to</returns>
        IObservable<ItemInfo> ListObjectsAsync(string bucketName, string prefix = null, bool recursive = false, CancellationToken cancellationToken = default);

        /// <summary>
        /// Get bucket policy
        /// </summary>
        /// <param name="bucketName">Bucket name</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        /// <returns>Returns Task with bucket policy json as string </returns>
        Task<string> GetPolicyAsync(string bucketName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sets the current bucket policy
        /// </summary>
        /// <param name="bucketName">Bucket Name</param>
        /// <param name="policyJson">policy json</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        /// <returns>Returns Task that sets the current bucket policy</returns>
        Task SetPolicyAsync(string bucketName, string policyJson, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the storage info (if available)
        /// </summary>
        /// <returns></returns>
        StorageInfo GetStorageInfo();

    }
}
