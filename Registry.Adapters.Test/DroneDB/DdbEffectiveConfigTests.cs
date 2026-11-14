using System;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Shouldly;

namespace Registry.Adapters.Test.DroneDB;

/// <summary><c>DDBGetEffectiveConfig()</c> parsed into <see cref="DdbEffectiveConfig"/>.</summary>
[TestFixture]
public class DdbEffectiveConfigTests
{
    [Test]
    public void Parse_ReadsEverySetting_TheCachesIncluded()
    {
        var config = DdbEffectiveConfig.Parse("""
            {"hardwareThreads": 8,
             "settings": {"artefactCacheMb": 4096, "cacheDir": "/c", "cpuPoolSize": 3, "gdalCacheMb": 256, "ioThreads": 2,
                          "minFreeDiskMb": 512},
             "sources": {"gdalCacheMb": "configure"}}
            """);

        config.ShouldBe(new DdbEffectiveConfig(512, 3, 2, "/c", 256, 4096));
    }

    /// <summary>A libddb from before the settings does not report them: 0, not a failure.</summary>
    [Test]
    public void Parse_WithoutTheCaches_Reports0()
    {
        var config = DdbEffectiveConfig.Parse("""{"settings": {"minFreeDiskMb": 512}}""");
        config.GdalCacheMb.ShouldBe(0);
        config.ArtefactCacheMb.ShouldBe(0);
    }

    [Test]
    public void Parse_WithoutSettings_IsAFormatError() =>
        Should.Throw<FormatException>(() => DdbEffectiveConfig.Parse("""{"sources": {}}"""));
}
