using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;

namespace Registry.Common
{

    /// <summary>
    /// This class is used to setup a test file system contained in a zip file
    /// </summary>
    public class TestFS : IDisposable
    {
        /// <summary>
        /// Path of test archive (zip file)
        /// </summary>
        public string TestArchivePath { get; }

        /// <summary>
        /// Generated test folder (root file system)
        /// </summary>
        public string TestFolder { get; }

        /// <summary>
        /// Base test folder for test grouping
        /// </summary>
        public string BaseTestFolder { get; }

        /// <summary>
        /// Creates a new instance of TestFS
        /// </summary>
        /// <param name="testArchivePath">The path of the test archive</param>
        /// <param name="baseTestFolder">The base test folder for test grouping</param>
        public TestFS(string testArchivePath, string baseTestFolder = "TestFS")
        {
            TestArchivePath = testArchivePath;
            BaseTestFolder = baseTestFolder;

            TestFolder = Path.Combine(Path.GetTempPath(), BaseTestFolder, CommonUtils.RandomString(16));

            Directory.CreateDirectory(TestFolder);

            if (!IsLocalPath(testArchivePath))
            {
                var uri = new Uri(testArchivePath);
                
                Debug.WriteLine($"Archive path is an url");

                var client = new WebClient();
                var tempPath = Path.Combine(Path.GetTempPath(), uri.Segments.Last());

                if (File.Exists(tempPath))
                {
                    Debug.WriteLine("No need to download, using cached one");
                }
                else
                {
                    Debug.WriteLine("Downloading archive");
                    client.DownloadFile(testArchivePath, tempPath);
                }

                ZipFile.ExtractToDirectory(tempPath, TestFolder);
                
                // NOTE: Let's leverage the temp folder
                // File.Delete(tempPath);

            }
            else
                ZipFile.ExtractToDirectory(TestArchivePath, TestFolder);
            

            Debug.WriteLine($"Created test FS '{TestArchivePath}' in '{TestFolder}'");

        }
        public void Dispose()
        {
            Debug.WriteLine($"Disposing test FS '{TestArchivePath}' in '{TestFolder}");
            Directory.Delete(TestFolder, true);
        }

        private static bool IsLocalPath(string path)
        {
            return path.StartsWith("file:/") ||
                   !path.StartsWith("http://") && (!path.StartsWith("https://") && !path.StartsWith("ftp://"));
        }

    }
}