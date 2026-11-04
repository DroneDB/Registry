using Microsoft.Extensions.Logging.Abstractions;
using Registry.Adapters;
using Registry.Ports.Archives;
using Registry.Web.Services.HeavyTasks;

namespace Registry.Web.Test.Support;

/// <summary>The shared archive extraction for tests, over the real file system and the test path rule.</summary>
public static class TestArchives
{
    public static DatasetArchiveExtractor Extractor(IArchiveExtractor archives, VolumeHeadroom? headroom = null) =>
        new(archives, TestPaths.Policy, TestScratch.Provider, new FileSystem(), headroom ?? TestHeadroom.Default,
            NullLogger<DatasetArchiveExtractor>.Instance);
}
