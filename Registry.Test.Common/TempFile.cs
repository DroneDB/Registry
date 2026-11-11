using System.Diagnostics;

namespace Registry.Test.Common;

/// <summary>
/// Temp file downloader for test fixtures with HTTP download and auto-cleanup on dispose.
/// The download goes through <see cref="TestDownloadCache"/>, so a partial file is never reused.
/// </summary>
public sealed class TempFile : IDisposable
{

    public string FilePath { get; }

    public TempFile(string url, string domain = "temp")
    {
        var folder = Path.Combine(Path.GetTempPath(), domain);

        FilePath = TestDownloadCache.GetOrDownload(url, folder);

        Debug.WriteLine("Temp file: " + FilePath);
    }

    public static void CleanDomain(string domain)
    {
        var folder = Path.Combine(Path.GetTempPath(), domain);
        if (Directory.Exists(folder))
        {
            TestCleanup.BeforeDelete?.Invoke(folder);
            Directory.Delete(folder, true);
        }
    }

    public void Dispose()
    {
        Debug.WriteLine("Deleting: " + FilePath);
        TestCleanup.BeforeDelete?.Invoke(FilePath);
        TestDownloadCache.Evict(FilePath);
        Debug.WriteLine("Deleted temp file");
    }
}
