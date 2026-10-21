#nullable enable
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Registry.Adapters;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test.Support;

/// <summary>
/// The scratch file provider for tests (RQ-10): the real <see cref="ScratchFileProvider"/> over the
/// real file system, its temp folder under the system temp folder.
/// </summary>
public static class TestScratch
{
    public static IScratchFileProvider Provider { get; } = For(Path.Combine(Path.GetTempPath(), "registry-test-scratch"));

    /// <summary>A provider whose temp folder is <paramref name="tempPath"/>.</summary>
    public static IScratchFileProvider For(string tempPath) =>
        new ScratchFileProvider(Microsoft.Extensions.Options.Options.Create(new ScratchOptions { TempPath = tempPath }), new FileSystem(),
            NullLogger<ScratchFileProvider>.Instance);
}
