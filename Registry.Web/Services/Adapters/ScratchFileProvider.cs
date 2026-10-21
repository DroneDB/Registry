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
/// <see cref="IScratchFileProvider"/> over <see cref="IFileSystem"/>: the configured temp folder
/// (<see cref="ScratchOptions.TempPath"/>) and the dataset uploads folder. Generalizes the upload
/// pipeline's temp files (same volume as the dataset, a new name every time).
/// </summary>
public sealed class ScratchFileProvider : IScratchFileProvider
{
    private readonly IFileSystem _fs;
    private readonly ILogger<ScratchFileProvider> _logger;
    private readonly Lazy<string> _root;

    public ScratchFileProvider(IOptions<ScratchOptions> options, IFileSystem fs, ILogger<ScratchFileProvider> logger)
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

    public string Root => _root.Value;

    public ScratchFile Create(string prefix, string extension)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(extension);
        return New(Path.Combine(Root, prefix + Guid.NewGuid().ToString("N") + extension));
    }

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
