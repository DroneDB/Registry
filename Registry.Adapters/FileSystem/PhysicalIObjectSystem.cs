using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Registry.Ports.FileSystem;
using Registry.Ports.ObjectSystem;

namespace Registry.Adapters.FileSystem
{
    /// <summary>
    /// Physical implementation of filesystem interface
    /// </summary>
    public class PhysicalIObjectSystem : IObjectSystem
    {
        private readonly string _baseFolder;

        public PhysicalIObjectSystem(string baseFolder)
        {
            _baseFolder = baseFolder;
        }

        public IEnumerable<string> EnumerateBuckets(string searchPattern, SearchOption searchOption)
        {
            return Directory.EnumerateDirectories(_baseFolder, searchPattern, searchOption);
        }

        public void CreateDirectory(string bucket, string path)
        {
            CheckPath(path);

            Directory.CreateDirectory(Path.Combine(_baseFolder, bucket, path));
        }

        private void CheckPath(string path)
        {
            if (path.Contains(".."))
                throw new InvalidOperationException("Parent path separator is not supported");
        }

        public void DeleteObject(string bucket, string path)
        {

            CheckPath(path);

            File.Delete(Path.Combine(_baseFolder, bucket, path));
        }

        public bool DirectoryExists(string bucket, string path)
        {
            return Directory.Exists(Path.Combine(_baseFolder, bucket, path));
        }

        public IEnumerable<string> EnumerateObjects(string bucket, string path, string searchPattern, SearchOption searchOption)
        {
            return Directory.EnumerateFiles(Path.Combine(_baseFolder, bucket, path), searchPattern, searchOption);
        }

        public IEnumerable<string> EnumerateFolders(string bucket, string path, string searchPattern, SearchOption searchOption)
        {
            return Directory.EnumerateDirectories(Path.Combine(_baseFolder, bucket, path), searchPattern, searchOption);
        }

        public bool ObjectExists(string bucket, string path)
        {
            return File.Exists(Path.Combine(_baseFolder, bucket, path));
        }

        public string ReadAllText(string bucket, string path, Encoding encoding)
        {
            return File.ReadAllText(Path.Combine(_baseFolder, bucket, path), encoding);
        }

        public byte[] ReadAllBytes(string bucket, string path)
        {
            return File.ReadAllBytes(Path.Combine(_baseFolder, bucket, path));
        }

        public void RemoveDirectory(string bucket, string path, bool recursive)
        {
            Directory.Delete(Path.Combine(_baseFolder, bucket, path), recursive);
        }

        public void WriteAllText(string bucket, string path, string contents, Encoding encoding)
        {
            File.WriteAllText(Path.Combine(_baseFolder, bucket, path), contents, encoding);
        }

        public void WriteAllBytes(string bucket, string path, byte[] contents)
        {
            File.WriteAllBytes(Path.Combine(_baseFolder, bucket, path), contents);
        }

        

    }
}
