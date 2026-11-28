using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities;

namespace Registry.Web.Services.OrganizationAssets;

/// <summary>
/// The files an organization owns, at <c>{StoragePath}/org-assets/{orgSlug}/{fileName}</c>: outside the public
/// branding folder, so only the endpoints that check access send them. Every name that reaches the disk has passed the
/// rules of a slug and of a file name, so none can leave the folder. What a file means (a report-template logo, the
/// logo of the organization) is the feature's: it picks the name and keeps the rules of it.
/// </summary>
public sealed partial class OrganizationAssetStore
{
    /// <summary>
    /// The file of the logo of the organization: it has no extension and no row in a database, so an organization has a
    /// logo exactly when this file exists, and a logo of another type replaces it instead of sitting beside it.
    /// </summary>
    public const string LogoFileName = "logo";

    private readonly IFileSystem _fs;
    private readonly ILogger<OrganizationAssetStore> _logger;
    private readonly string _root;

    public OrganizationAssetStore(IFileSystem fs, IOptions<StorageOptions> storage, ILogger<OrganizationAssetStore> logger)
    {
        _fs = fs;
        _logger = logger;
        _root = RootOf(storage.Value);
    }

    // Lower case letters, digits, dashes and dots, starting with a letter or a digit: no separator and no ".."
    [GeneratedRegex(@"^[a-z0-9][a-z0-9.-]{0,127}\z")]
    private static partial Regex FileNamePattern();

    /// <summary>The folder that holds the folders of the organizations.</summary>
    public static string RootOf(StorageOptions storage) =>
        Path.GetFullPath(Path.Combine(storage.StoragePath ?? ".", MagicStrings.OrganizationAssetsFolder));

    /// <summary>
    /// Stores <paramref name="content"/> as the file <paramref name="fileName"/> of the organization. It is written whole
    /// to a file of its own beside its place and then renamed, so a reader never sees a part, and two writers never
    /// write one file. A file of that name is replaced when <paramref name="replace"/>, the last writer winning, and
    /// otherwise it is an <see cref="IOException"/>.
    /// </summary>
    public void Save(string orgSlug, string fileName, byte[] content, bool replace)
    {
        var path = PathOf(orgSlug, fileName);

        _fs.FolderCreate(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            _fs.WriteAllBytes(temporary, content);
            _fs.Move(temporary, path, overwrite: replace);
        }
        finally
        {
            if (_fs.Exists(temporary))
                _fs.Delete(temporary);
        }
    }

    /// <summary>The file, or null when it is not there.</summary>
    public string? Find(string orgSlug, string fileName)
    {
        var path = PathOf(orgSlug, fileName);
        return _fs.Exists(path) ? path : null;
    }

    /// <summary>
    /// The state of the file on the disk (read once, so its time and its length are those of one moment), or null when it
    /// is not there.
    /// </summary>
    public FileInfo? InfoOf(string orgSlug, string fileName)
    {
        var file = new FileInfo(PathOf(orgSlug, fileName));
        return file.Exists ? file : null;
    }

    /// <summary>The first <paramref name="count"/> bytes of the file (fewer when it is shorter), or null when it is not there.</summary>
    public byte[]? ReadHead(string orgSlug, string fileName, int count)
    {
        try
        {
            using var stream = _fs.OpenRead(PathOf(orgSlug, fileName));
            var head = new byte[count];
            var read = stream.ReadAtLeast(head, count, throwOnEndOfStream: false);
            return head.AsSpan(0, read).ToArray();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Deletes the file (nothing when it is already gone). A failure of the disk is thrown.</summary>
    public void Remove(string orgSlug, string fileName)
    {
        var path = PathOf(orgSlug, fileName);
        if (_fs.Exists(path))
            _fs.Delete(path);
    }

    /// <summary>Deletes the file (nothing when it is already gone). A failure is logged, never thrown: nothing names the file any more.</summary>
    public void Delete(string orgSlug, string fileName) =>
        Quietly(() => Remove(orgSlug, fileName),
            ex => _logger.LogWarning(ex, "The file {FileName} of organization {OrgSlug} could not be deleted",
                fileName, orgSlug));

    /// <summary>Deletes every file of an organization (its folder), with the organization. A failure is logged, never thrown.</summary>
    public void DeleteOrganization(string orgSlug)
    {
        var folder = FolderOf(orgSlug);
        Quietly(() =>
        {
            if (_fs.FolderExists(folder))
                _fs.FolderDelete(folder);
        }, ex => _logger.LogWarning(ex, "The files of organization {OrgSlug} could not be deleted", orgSlug));
    }

    // A removal of files that nothing names any more: a failure of the disk is the caller's log line, never its error
    private static void Quietly(Action remove, Action<Exception> logFailure)
    {
        try
        {
            remove();
        }
        catch (IOException ex)
        {
            logFailure(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            logFailure(ex);
        }
    }

    private string FolderOf(string orgSlug)
    {
        if (!SlugRules.IsValid(orgSlug))
            throw new ArgumentException("Not an organization slug", nameof(orgSlug));

        return Path.Combine(_root, orgSlug);
    }

    private string PathOf(string orgSlug, string fileName)
    {
        if (!FileNamePattern().IsMatch(fileName) || fileName.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Not a file name of an organization", nameof(fileName));

        return Path.Combine(FolderOf(orgSlug), fileName);
    }
}
