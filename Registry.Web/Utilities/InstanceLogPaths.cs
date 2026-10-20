#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;

namespace Registry.Web.Utilities;

/// <summary>
/// One log file per instance type. The web server, the processing nodes and the thumbnail generator
/// share the storage folder, and the File sink's <c>shared: true</c> serializes writers with a named
/// mutex that does not cross containers: each instance type writes its own file, named after the
/// configured one (<c>./logs/registry.txt</c> becomes <c>./logs/registry-web.txt</c>, then rolled as
/// <c>registry-web20261020.txt</c>). The default all-in-one instance keeps the configured name.
/// </summary>
internal static class InstanceLogPaths
{
    private const string WriteToSection = "Serilog:WriteTo";
    private const string FileSinkName = "File";
    private const string PathArgument = "Args:path";

    /// <summary>The file name suffix of <paramref name="instanceType"/>; <c>null</c> keeps the configured name.</summary>
    public static string? Suffix(InstanceType instanceType) => instanceType switch
    {
        InstanceType.WebServer => "web",
        InstanceType.ProcessingNode => "node",
        InstanceType.ThumbnailGenerator => "thumbs",
        _ => null
    };

    /// <summary>
    /// <paramref name="configuredPath"/> with the instance suffix before the extension, in the same folder.
    /// </summary>
    public static string? PathFor(string? configuredPath, InstanceType instanceType)
    {
        var suffix = Suffix(instanceType);
        if (suffix == null || string.IsNullOrWhiteSpace(configuredPath))
            return configuredPath;

        var name = $"{Path.GetFileNameWithoutExtension(configuredPath)}-{suffix}{Path.GetExtension(configuredPath)}";
        var folder = Path.GetDirectoryName(configuredPath);

        return string.IsNullOrEmpty(folder) ? name : Path.Combine(folder, name);
    }

    /// <summary>
    /// The instance type set in <paramref name="configuration"/> (key <c>InstanceType</c>, name or
    /// number); <see cref="InstanceType.Default"/> when absent or not recognized.
    /// </summary>
    public static InstanceType InstanceTypeOf(IConfiguration configuration) =>
        Enum.TryParse<InstanceType>(configuration[Program.InstanceTypeKey], true, out var instanceType) &&
        Enum.IsDefined(instanceType)
            ? instanceType
            : InstanceType.Default;

    /// <summary>
    /// <paramref name="configuration"/> with the path of every top-level <c>File</c> sink of
    /// <c>Serilog:WriteTo</c> made per instance (see <see cref="PathFor"/>); the same instance when
    /// nothing changes. The source configuration is not modified: the paths are layered over it.
    /// </summary>
    public static IConfiguration Apply(IConfiguration configuration)
    {
        var instanceType = InstanceTypeOf(configuration);
        if (Suffix(instanceType) == null)
            return configuration;

        var overrides = new Dictionary<string, string?>();
        foreach (var sink in configuration.GetSection(WriteToSection).GetChildren())
        {
            if (!string.Equals(sink["Name"], FileSinkName, StringComparison.OrdinalIgnoreCase))
                continue;

            var configuredPath = sink[PathArgument];
            var instancePath = PathFor(configuredPath, instanceType);
            if (instancePath != configuredPath)
                overrides[ConfigurationPath.Combine(sink.Path, PathArgument)] = instancePath;
        }

        return overrides.Count == 0
            ? configuration
            : new ConfigurationBuilder().AddConfiguration(configuration).AddInMemoryCollection(overrides).Build();
    }
}
