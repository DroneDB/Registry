using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Registry.Ports.FileSystem;

namespace Registry.Adapters.FileSystem
{
    public class S3FileSystem : IFileSystem
    {
        public void CreateDirectory(string path)
        {
            throw new NotImplementedException();
        }

        public void DeleteFile(string path)
        {
            throw new NotImplementedException();
        }

        public bool DirectoryExists(string path)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<string> EnumerateFolders(string path, string searchPattern, SearchOption searchOption)
        {
            throw new NotImplementedException();
        }

        public bool FileExists(string path)
        {
            throw new NotImplementedException();
        }

        public string ReadAllText(string path, Encoding encoding)
        {
            throw new NotImplementedException();
        }

        public byte[] ReadAllBytes(string path)
        {
            throw new NotImplementedException();
        }

        public void RemoveDirectory(string path, bool recursive)
        {
            throw new NotImplementedException();
        }

        public void WriteAllText(string path, string contents, Encoding encoding)
        {
            throw new NotImplementedException();
        }

        public void WriteAllBytes(string path, byte[] contents)
        {
            throw new NotImplementedException();
        }
    }
}
