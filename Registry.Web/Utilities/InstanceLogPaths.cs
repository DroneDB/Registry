using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace Registry.Web.Utilities;

/// <summary>
/// One log file per instance type. The web server, the processing nodes and the thumbnail generator
/// share the storage folder, and the File sink's <c>shared: true</c> serializes writers with a named
/// mutex that does not cross containers: each instance type writes its own file, named after the
/// configured one (<c>./logs/registry.txt</c> becomes <c>./logs/registry-web.txt</c>, then rolled as
/// <c>registry-web20261020.txt</c>). The default all-in-one instance keeps the configured name.
/// Several processing nodes can share the folder (docker-compose <c>registry_node_1</c>,
/// <c>registry_node_2</c>), so the node file also carries the sanitised machine name
/// (<c>registry-node-&lt;host&gt;.txt</c>); give each node container a fixed <c>hostname</c> so the name,
/// and the retention that applies per file name, survive container recreation.
/// </summary>
internal static class InstanceLogPaths
{
    private const string WriteToSection = "Serilog:WriteTo";
    private const string FileSinkName = "File";
    private const string PathArgument = "Args:path";

    /// <summary>
    /// The file name suffix of <paramref name="instanceType"/> on <paramref name="machineName"/>;
    /// <c>null</c> keeps the configured name. Only the processing node, of which a deployment runs
    /// several, carries the machine name.
    /// </summary>
    public static string? Suffix(InstanceType instanceType, string? machineName) => instanceType switch
    {
        InstanceType.WebServer => "web",
        InstanceType.ProcessingNode => WithMachineName("node", machineName),
        InstanceType.ThumbnailGenerator => "thumbs",
        _ => null
    };

    /// <summary>
    /// <paramref name="suffix"/> followed by <paramref name="machineName"/> made file-name safe
    /// (lower case, letters, digits and dashes); <paramref name="suffix"/> alone when nothing is left.
    /// </summary>
    private static string WithMachineName(string suffix, string? machineName)
    {
        var host = new string((machineName ?? string.Empty).Trim().ToLowerInvariant()
                .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-');

        return host.Length == 0 ? suffix : $"{suffix}-{host}";
    }

    /// <summary>
    /// <paramref name="configuredPath"/> with the instance suffix (see <see cref="Suffix"/>) before the
    /// extension, in the same folder.
    /// </summary>
    public static string? PathFor(string? configuredPath, InstanceType instanceType, string? machineName)
    {
        var suffix = Suffix(instanceType, machineName);
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
    /// <c>Serilog:WriteTo</c> made per instance on this machine (see <see cref="PathFor"/>); the same
    /// instance when nothing changes. The source configuration is not modified: the paths are layered
    /// over it.
    /// </summary>
    public static IConfiguration Apply(IConfiguration configuration) =>
        Apply(configuration, Environment.MachineName);

    /// <inheritdoc cref="Apply(IConfiguration)"/>
    /// <param name="configuration">The host configuration.</param>
    /// <param name="machineName">The machine the instance runs on.</param>
    public static IConfiguration Apply(IConfiguration configuration, string? machineName)
    {
        var instanceType = InstanceTypeOf(configuration);
        if (Suffix(instanceType, machineName) == null)
            return configuration;

        var overrides = new Dictionary<string, string?>();
        foreach (var sink in configuration.GetSection(WriteToSection).GetChildren())
        {
            if (!string.Equals(sink["Name"], FileSinkName, StringComparison.OrdinalIgnoreCase))
                continue;

            var configuredPath = sink[PathArgument];
            var instancePath = PathFor(configuredPath, instanceType, machineName);
            if (instancePath != configuredPath)
                overrides[ConfigurationPath.Combine(sink.Path, PathArgument)] = instancePath;
        }

        return overrides.Count == 0
            ? configuration
            : new ConfigurationBuilder().AddConfiguration(configuration).AddInMemoryCollection(overrides).Build();
    }
}
