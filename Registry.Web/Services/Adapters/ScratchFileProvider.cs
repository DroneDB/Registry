#nullable enable
using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Where the Registry writes files that are not (yet) part of a dataset (RQ-10). Two places, by what
/// happens to the file next:
/// <list type="bullet">
/// <item><see cref="Create"/>: under the configured temp folder (<c>StorageOptions.TempPath</c>), for a
/// file that is read and deleted (a downloaded archive, an export, a task folder).</item>
/// <item><see cref="CreateForDataset"/>: in the dataset's uploads folder, the same volume as the
/// dataset, for a file that is moved into it later: the move is a rename, never a copy.</item>
/// </list>
/// </summary>
public sealed class ScratchFileProvider
{
    private readonly IFileSystem _fs;
    private readonly ILogger<ScratchFileProvider> _logger;
    private readonly Lazy<string> _root;

    public ScratchFileProvider(IOptions<StorageOptions> options, IFileSystem fs, ILogger<ScratchFileProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _fs = fs;
        _logger = logger;
        _root = new Lazy<string>(() =>
        {
            var configured = options.Value.TempPath;
            if (string.IsNullOrWhiteSpace(configured))
                throw new InvalidOperationException("AppSettings:TempPath is not set: there is no folder for temporary files.");
            var root = Path.GetFullPath(configured);
            _fs.FolderCreate(root);
            return root;
        });
    }

    /// <summary>The configured temp folder, absolute (created on first use).</summary>
    public string Root => _root.Value;

    /// <summary>A new file name under <see cref="Root"/>: <paramref name="prefix"/>, a GUID, <paramref name="extension"/>.</summary>
    public ScratchFile Create(string prefix, string extension)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(extension);
        return New(Path.Combine(Root, prefix + Guid.NewGuid().ToString("N") + extension));
    }

    /// <summary>
    /// A new file name in the uploads folder of the dataset at <paramref name="datasetFolder"/>
    /// (created on first use). The abandoned-upload sweep removes a <c>.tmp</c> file left there.
    /// </summary>
    public ScratchFile CreateForDataset(string datasetFolder, string extension = ".tmp")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetFolder);
        ArgumentNullException.ThrowIfNull(extension);

        var folder = Path.Combine(datasetFolder, IDDB.UploadsFolderName);
        _fs.FolderCreate(folder);
        return New(Path.Combine(folder, Guid.NewGuid().ToString("N") + extension));
    }

    private ScratchFile New(string path) => new(path, _fs,
        (file, ex) => _logger.LogWarning(ex, "Could not delete the scratch file '{Path}'", file));
}
