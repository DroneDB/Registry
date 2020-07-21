using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Minio.DataModel;
using Registry.Ports.ObjectSystem.Model;
using SSEC = Minio.DataModel.SSEC;

namespace Registry.Adapters
{
    public static class AdaptersUtils
    {
        public static ServerSideEncryption ToSSE(this IServerEncryption encryption)
        {
            if (encryption is EncryptionC ssec)
            {
                return new SSEC(ssec.Key);
            }

            if (encryption is EncryptionKMS ssekms)
            {
                return new SSEKMS(ssekms.Key, ssekms.Context);
            }

            if (encryption is EncryptionS3 sses3)
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
                // without dashes
                .Replace("-", string.Empty)
                // make lowercase
                .ToLower();

            //var multipartHash = concatHash.ToArray().GetHash(md5).ToHexString();
            return multipartHash + "-" + multipartSplitCount;
        }

        private static ArraySegment<T> GetSegment<T>(this T[] array, int offset, int? count = null)
        {
            count ??= array.Length - offset;
            return new ArraySegment<T>(array, offset, count.Value);
        }

        public static string ToHashString(this byte[] hash)
        {
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }

        public static string ToHexString(this byte[] bytes)
        {
            var chars = new char[bytes.Length * 2 + 2];

            for (int i = 0; i < bytes.Length; i++)
            {
                chars[2 * i + 2] = ToHexDigit(bytes[i] / 16);
                chars[2 * i + 3] = ToHexDigit(bytes[i] % 16);
            }

            return new string(chars).ToLower();
        }

        private static char ToHexDigit(int i)
        {
            if (i < 10)
            {
                return (char)(i + '0');
            }
            return (char)(i - 10 + 'A');
        }

        #endregion
    }
}
