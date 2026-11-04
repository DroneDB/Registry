using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Ports.Import;
using Registry.Web.Services.Import;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Tests for <see cref="ImportSourceFactory"/>: case-insensitive resolution by source type and a clear
/// failure for unknown types.
/// </summary>
[TestFixture]
public class ImportSourceFactoryTests
{
    private sealed class FakeSource(string type) : IImportSource
    {
        public string SourceType => type;

        public Task<ImportSourceProbe> ProbeAsync(JsonElement parameters, CancellationToken ct)
            => Task.FromResult(new ImportSourceProbe(true, null, null, null, null));

        public Task FetchAsync(JsonElement parameters, string destFolder, IProgress<ImportProgress> progress,
            CancellationToken ct) => Task.CompletedTask;
    }

    private static ImportSourceFactory Factory()
        => new([new FakeSource("registry"), new FakeSource("archive-url")]);

    [Test]
    public void Resolve_KnownType_ReturnsSource()
    {
        Factory().Resolve("registry").SourceType.ShouldBe("registry");
    }

    [Test]
    public void Resolve_IsCaseInsensitive()
    {
        Factory().Resolve("ARCHIVE-URL").SourceType.ShouldBe("archive-url");
    }

    [Test]
    public void Resolve_UnknownType_Throws()
    {
        Should.Throw<ArgumentException>(() => Factory().Resolve("ftp"));
    }

    [Test]
    public void AvailableTypes_ListsAllRegisteredSources()
    {
        var types = Factory().AvailableTypes;
        types.ShouldContain("registry");
        types.ShouldContain("archive-url");
        types.Count.ShouldBe(2);
    }
}
