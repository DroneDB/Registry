using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Registry.Common
{
    public static class CommonExtenders
    {
        /// <summary>
        /// Safe reset to the beginning
        /// </summary>
        /// <param name="stream"></param>
        public static void Reset(this Stream stream)
        {
            if (!stream.CanSeek)
                throw new InvalidOperationException("Stream does not support seeking");
            
            stream.Seek(0, SeekOrigin.Begin);
        }
        
        /// <summary>
        /// Safe reset to the beginning
        /// </summary>
        /// <param name="stream"></param>
        public static void SafeReset(this Stream stream)
        {
            if (stream.CanSeek)
                stream.Seek(0, SeekOrigin.Begin);
        }

        public static string ToS3Path(this string path)
        {
            return path.Replace('\\', '/');
        }

        private const int DefaultCopyBufferSize = 81920;

        public static void CopyToMultiple(this Stream source, params Stream[] destinations)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destinations == null || destinations.Any(s => s == null)) 
                throw new ArgumentNullException(nameof(destinations));

            if (!source.CanRead)
                throw new NotSupportedException("Reading is not supported on the source stream");

            if (destinations.Any(s => !s.CanWrite))
                throw new NotSupportedException("All the destination streams should be writable");
            
            var buffer = ArrayPool<byte>.Shared.Rent(DefaultCopyBufferSize);
            try
            {
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
                {
                    foreach (var destination in destinations)
                        destination.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

    }
}