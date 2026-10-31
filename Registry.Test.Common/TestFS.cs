using System.Diagnostics;
using System.IO.Compression;
using Registry.Common;

namespace Registry.Test.Common;

/// <summary>
/// This class is used to setup a test file system contained in a zip file
/// </summary>
public sealed class TestFS : IDisposable
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

    private readonly string? _oldCurrentDirectory;

    /// <summary>
    /// Creates a new instance of TestFS
    /// </summary>
    /// <param name="testArchivePath">The path of the test archive</param>
    /// <param name="baseTestFolder">The base test folder for test grouping</param>
    /// <param name="setCurrentDirectory">If it should set the current directory to the test directory</param>
    public TestFS(string testArchivePath, string baseTestFolder = "TestFS", bool setCurrentDirectory = false)
    {
        TestArchivePath = testArchivePath;
        BaseTestFolder = baseTestFolder;

        TestFolder = Path.Combine(Path.GetTempPath(), BaseTestFolder, CommonUtils.RandomString(16));

        Directory.CreateDirectory(TestFolder);

        if (!IsLocalPath(testArchivePath))
        {
            Debug.WriteLine("Archive path is an url");

            // Downloaded archives are cached (atomically, hash-checked) next to the test folders.
            var archive = TestDownloadCache.GetOrDownload(testArchivePath,
                Path.Combine(Path.GetTempPath(), baseTestFolder));

            ZipFile.ExtractToDirectory(archive, TestFolder);
        }
        else
            ZipFile.ExtractToDirectory(TestArchivePath, TestFolder);

        Debug.WriteLine($"Created test FS '{TestArchivePath}' in '{TestFolder}'");

        if (setCurrentDirectory)
        {
            _oldCurrentDirectory = Environment.CurrentDirectory;
            Environment.CurrentDirectory = TestFolder;

            Debug.WriteLine($"Set current directory to '{TestFolder}'");
        }

    }

    public void Dispose()
    {

        if (_oldCurrentDirectory != null)
        {
            Environment.CurrentDirectory = _oldCurrentDirectory;
            Debug.WriteLine($"Restored current directory to '{_oldCurrentDirectory}'");
        }

        Debug.WriteLine($"Disposing test FS '{TestArchivePath}' in '{TestFolder}");
        try
        {
            Directory.Delete(TestFolder, true);
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"Cannot recursive delete temp folder: {ex.Message}");
            Debug.WriteLine($"Consider manual cleanup of folder '{TestFolder}'");
        }
    }

    private static bool IsLocalPath(string path)
    {
        return path.StartsWith("file:/", StringComparison.Ordinal) ||
               !path.StartsWith("http://", StringComparison.Ordinal) &&
               !path.StartsWith("https://", StringComparison.Ordinal) &&
               !path.StartsWith("ftp://", StringComparison.Ordinal);
    }

    public static void ClearCache(string baseTestFolder)
    {
        var folder = Path.Combine(Path.GetTempPath(), baseTestFolder);
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
    }
}
