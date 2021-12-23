using System.Collections.Generic;
using System.IO;
using System.Text;
using Registry.Ports;

namespace Registry.Adapters
{
    public class FileSystem : IFileSystem
    {
        public string ReadAllText(string path)
        {
            return File.ReadAllText(path);
        }

        public void WriteAllText(string path, string contents)
        {
            File.WriteAllText(path, contents);
        }

        public void WriteAllText(string path, string contents, Encoding encoding)
        {
            File.WriteAllText(path, contents, encoding);
        }

        public void WriteAllBytes(string path, byte[] bytes)
        {
            File.WriteAllBytes(path, bytes);
        }

        public void AppendAllText(string path, string contents)
        {
            File.AppendAllText(path, contents);
        }

        public void AppendAllText(string path, string contents, Encoding encoding)
        {
            File.AppendAllText(path, contents, encoding);
        }

        public void AppendAllLines(string path, IEnumerable<string> contents)
        {
            File.AppendAllLines(path, contents);
        }

        public void AppendAllLines(string path, IEnumerable<string> contents, Encoding encoding)
        {
            File.AppendAllLines(path, contents, encoding);
        }

        public void Copy(string sourceFileName, string destFileName, bool overwrite = true)
        {
            File.Copy(sourceFileName, destFileName, overwrite);
        }

        public void FolderCopy(string sourceFileName, string destFileName, bool overwrite = true, bool recursive = true)
        {
            Directory.CreateDirectory(destFileName);
            foreach (var file in Directory.GetFiles(sourceFileName))
            {
                File.Copy(file, Path.Combine(destFileName, Path.GetFileName(file)), overwrite);
            }

            if (recursive)
            {
                foreach (var dir in Directory.GetDirectories(sourceFileName))
                {
                    FolderCopy(dir, Path.Combine(destFileName, Path.GetFileName(dir)), overwrite, recursive);
                }
            }
        }
        
        public void Move(string sourceFileName, string destFileName)
        {
            File.Move(sourceFileName, destFileName);
        }
        
        public void FolderMove(string sourceFileName, string destFileName)
        {
            Directory.Move(sourceFileName, destFileName);
        }

        public void Replace(string sourceFileName, string destinationFileName, string destinationBackupFileName)
        {
            File.Replace(sourceFileName, destinationFileName, destinationBackupFileName);
        }

        public void Replace(string sourceFileName, string destinationFileName, string destinationBackupFileName, bool ignoreMetadataErrors)
        {
            File.Replace(sourceFileName, destinationFileName, destinationBackupFileName, ignoreMetadataErrors);
        }

        public void Delete(string path)
        {
            File.Delete(path);
        }
        
        public void FolderDelete(string path, bool recursive = true)
        {
            Directory.Delete(path, recursive);
        }
        
        public bool Exists(string path)
        {
            return File.Exists(path);
        }
        
        public bool FolderExists(string path)
        {
            return Directory.Exists(path);
        }
        
        public void FolderCreate(string path)
        {
            Directory.CreateDirectory(path);
        }

    }
}