using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MimeMapping;
using Minio.DataModel;
using Registry.Adapters.ObjectSystem;
using Registry.Adapters.ObjectSystem.Model;
using Registry.Common;
using Registry.Ports.ObjectSystem;
using Registry.Ports.ObjectSystem.Model;
using CopyConditions = Minio.DataModel.CopyConditions;
using SSEC = Minio.DataModel.SSEC;

namespace Registry.Adapters
{
    public static class AdaptersUtils
    {
        public static ServerSideEncryption ToSSE(this IServerEncryption encryption)
        {

            if (encryption == null) return null;

            if (encryption is EncryptionC ssec)
            {
                return new SSEC(ssec.Key);
            }

            if (encryption is EncryptionKMS ssekms)
            {
                return new SSEKMS(ssekms.Key, ssekms.Context);
            }

            if (encryption is EncryptionS3)
            {
                return new SSES3();
            }

            if (encryption is EncryptionCopy ssecopy)
            {
                return new SSECopy(ssecopy.Key);
            }

            throw new ArgumentException($"Encryption not supported: '{encryption.GetType().Name}'");

        }

        #region ETag

        // Credit to https://stackoverflow.com/questions/12186993/what-is-the-algorithm-to-compute-the-amazon-s3-etag-for-a-file-larger-than-5gb#answer-19896823

        public static byte[] GetHash(this byte[] array, HashAlgorithm algorithm)
        {
            var hash = algorithm.ComputeHash(array);
            return hash;
        }

        public static string CalculateMultipartEtag(Stream stream, int chunkCount)
        {
            var multipartSplitCount = 0;
            var chunkSize = 1024 * 1024 * chunkCount;
            var splitCount = stream.Length / chunkSize;
            var concatHash = new List<byte>();

            using var md5 = MD5.Create();

            var buffer = new byte[chunkSize];

            for (var i = 0; i <= splitCount; i++)
            {
                var cnt = stream.Read(buffer, 0, chunkSize);
                if (cnt == 0) break;

                var chunk = cnt != chunkSize ? buffer.Take(cnt).ToArray() : buffer;
                var hash = chunk.GetHash(md5);
                concatHash.AddRange(hash);
                multipartSplitCount++;
            }

            string multipartHash = BitConverter.ToString(concatHash.ToArray())
                .Replace("-", string.Empty)
                .ToLowerInvariant();

            return multipartHash + "-" + multipartSplitCount;

        }

        public static string CalculateMultipartEtag(byte[] array, int chunkCount)
        {
            var multipartSplitCount = 0;
            var chunkSize = 1024 * 1024 * chunkCount;
            var splitCount = array.Length / chunkSize;
            var mod = array.Length - chunkSize * splitCount;
            var concatHash = new List<byte>();

            using var md5 = MD5.Create();

            for (var i = 0; i < splitCount; i++)
            {
                var offset = i == 0 ? 0 : chunkSize * i;
                var chunk = GetSegment(array, offset, chunkSize);
                var hash = chunk.ToArray().GetHash(md5);
                concatHash.AddRange(hash);
                multipartSplitCount++;
            }
            if (mod != 0)
            {
                var chunk = GetSegment(array, chunkSize * splitCount, mod);
                var hash = chunk.ToArray().GetHash(md5);
                concatHash.AddRange(hash);
                multipartSplitCount++;
            }

            string multipartHash = BitConverter.ToString(concatHash.ToArray())
                .Replace("-", string.Empty)
                .ToLowerInvariant();

            //var multipartHash = concatHash.ToArray().GetHash(md5).ToHexString();
            return multipartHash + "-" + multipartSplitCount;
        }

        private static ArraySegment<T> GetSegment<T>(this T[] array, int offset, int? count = null)
        {
            count ??= array.Length - offset;
            return new ArraySegment<T>(array, offset, count.Value);
        }


        public static string CalculateETag(FileInfo info)
        {
            // 2GB
            var chunkSize = 2L * 1024 * 1024 * 1024;

            var parts = info.Length == 0 ? 1 : (int)Math.Ceiling((double)info.Length / chunkSize);

            using var stream = info.OpenRead();

            return CalculateMultipartEtag(stream, parts);

        }

        public static string CalculateETag(string filePath)
        {
            return CalculateETag(new FileInfo(filePath));
        }


        #endregion

        public static ObjectInfoDto GenerateObjectInfo(string filePath, string objectName = null)
        {

            var fileInfo = new FileInfo(filePath);

            var objectInfo = new ObjectInfoDto
            {
                MetaData = new Dictionary<string, string>(),
                ContentType = MimeUtility.GetMimeMapping(filePath),
                ETag = CalculateETag(fileInfo),
                LastModified = File.GetLastWriteTime(filePath),
                Name = objectName ?? fileInfo.Name,
                Size = fileInfo.Length
            };

            return objectInfo;

        }

        public static CopyConditions ToS3CopyConditions(
            this Ports.ObjectSystem.Model.CopyConditions copyConditions)
        {
            if (copyConditions == null) throw new ArgumentNullException(nameof(copyConditions));

            var cc = new CopyConditions();

            var copyConditionsField = typeof(CopyConditions).GetField("copyConditions", BindingFlags.NonPublic | BindingFlags.Instance);

            if (copyConditionsField == null)
                throw new InvalidOperationException("Expected copyConditions field not found");

            if (!(copyConditionsField.GetValue(cc) is Dictionary<string, string> obj))
                throw new InvalidOperationException("Expected copyConditions field value not found");

            foreach (var (key, value) in copyConditions.GetConditions())
                obj.Add(key, value);

            cc.SetByteRange(copyConditions.ByteRangeStart, copyConditions.ByteRangeEnd);

            return cc;
        }

        public static async Task MoveDirectory(this IObjectSystem system, string bucketName, string source, string dest)
        {
            var sourceObjects = await system.ListObjectsAsync(bucketName, source, true).ToArray();

            foreach (var obj in sourceObjects)
            {

                if (obj.IsDir)
                    throw new NotSupportedException("Not supported folder copy");
                

                var newPath = CommonUtils.SafeCombine(dest, obj.Key[(source.Length + 1)..]);

                await system.CopyObjectAsync(bucketName, obj.Key, bucketName, newPath);
                await system.RemoveObjectAsync(bucketName, obj.Key);

            }

        }
    }
}
