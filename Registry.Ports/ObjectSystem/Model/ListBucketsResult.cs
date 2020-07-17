using System.Collections.Generic;
using Registry.Ports.FileSystem.Model;

namespace Registry.Ports.ObjectSystem.Model
{
    public class ListBucketsResult
    {
        public string Owner { get; set; }
        public IEnumerable<BucketInfo> Buckets { get; set; }
    }
}