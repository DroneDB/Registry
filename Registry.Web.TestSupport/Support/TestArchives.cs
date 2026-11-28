using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Registry.Adapters;
using Registry.Ports.Archives;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks;

namespace Registry.Web.Test.Support;

/// <summary>The shared archive extraction for tests, over the real file system and the test path rule.</summary>
public static class TestArchives
{
    public static DatasetArchiveExtractor Extractor(IArchiveExtractor archives, VolumeHeadroom? headroom = null,
        ProcessingPlatformSettings? settings = null) =>
        new(archives, TestPaths.Policy, TestScratch.Provider, new FileSystem(), headroom ?? TestHeadroom.Default,
            Settings(settings), NullLogger<DatasetArchiveExtractor>.Instance);

    /// <summary>The settings of the extraction: <paramref name="settings"/>, else the defaults.</summary>
    public static IOptions<ProcessingPlatformSettings> Settings(ProcessingPlatformSettings? settings = null) =>
        Microsoft.Extensions.Options.Options.Create(settings ?? new ProcessingPlatformSettings());
}
