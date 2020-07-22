using System;
using System.Collections.Generic;

namespace Registry.Ports.ObjectSystem.Model
{
    public class ObjectInfo
    {
        /// <summary>
        /// Object metadata information.
        /// </summary>
        /// <param name="objectName">Object name</param>
        /// <param name="size">Object size</param>
        /// <param name="lastModified">Last when object was modified</param>
        /// <param name="etag">Unique entity tag for the object</param>
        /// <param name="contentType">Object content type</param>
        /// <param name="metadata"></param>
        public ObjectInfo(string objectName, long size, DateTime lastModified, string etag, string contentType,
            Dictionary<string, string> metadata)
        {
            ObjectName = objectName;
            Size = size;
            LastModified = lastModified;
            ETag = etag;
            ContentType = contentType;
            MetaData = metadata;
        }

        public string ObjectName { get; }
        public long Size { get; }
        public DateTime LastModified { get; }
        public string ETag { get; }
        public string ContentType { get; }
        public IReadOnlyDictionary<string, string> MetaData { get; }

        public override string ToString()
        {
            return $"{ObjectName} : Size({Size}) LastModified({LastModified}) ETag({ETag}) Content-Type({ContentType})";
        }
    }
}