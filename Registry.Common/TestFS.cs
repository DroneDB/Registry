using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;

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

            ZipFile.ExtractToDirectory(TestArchivePath, TestFolder);

            Debug.WriteLine($"Created test FS '{TestArchivePath}' in '{TestFolder}'");
        }
        public void Dispose()
        {
            Debug.WriteLine($"Disposing test FS '{TestArchivePath}' in '{TestFolder}");
            Directory.Delete(TestFolder, true);
        }

    }
}