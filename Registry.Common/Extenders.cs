using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Registry.Common
{
    public static class Extenders
    {
        /// <summary>
        /// Safe reset to the beginning
        /// </summary>
        /// <param name="stream"></param>
        public static void Reset(this Stream stream)
        {
            if (stream.CanSeek)
                stream.Seek(0, SeekOrigin.Begin);
        }

        public static string ToS3Path(this string path)
        {
            return path.Replace('\\', '/');
        }
    }
}