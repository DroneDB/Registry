using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Registry.Ports.FileSystem;
using Registry.Ports.ObjectSystem;

namespace Registry.Adapters.FileSystem
{
    public class S3ObjectSystem : IObjectSystem
    {
        public IEnumerable<string> EnumerateBuckets(string searchPattern, SearchOption searchOption)
        {
            throw new NotImplementedException();
        }

        public void CreateDirectory(string bucket, string path)
        {
            throw new NotImplementedException();
        }

        public void DeleteObject(string bucket, string path)
        {
            throw new NotImplementedException();
        }

        public bool DirectoryExists(string bucket, string path)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<string> EnumerateObjects(string bucket, string path, string searchPattern, SearchOption searchOption)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<string> EnumerateFolders(string bucket, string path, string searchPattern, SearchOption searchOption)
        {
            throw new NotImplementedException();
        }

        public bool ObjectExists(string bucket, string path)
        {
            throw new NotImplementedException();
        }

        public string ReadAllText(string bucket, string path, Encoding encoding)
        {
            throw new NotImplementedException();
        }

        public byte[] ReadAllBytes(string bucket, string path)
        {
            throw new NotImplementedException();
        }

        public void RemoveDirectory(string bucket, string path, bool recursive)
        {
            throw new NotImplementedException();
        }

        public void WriteAllText(string bucket, string path, string contents, Encoding encoding)
        {
            throw new NotImplementedException();
        }

        public void WriteAllBytes(string bucket, string path, byte[] contents)
        {
            throw new NotImplementedException();
        }
    }
}
