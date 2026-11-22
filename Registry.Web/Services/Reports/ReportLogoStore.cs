using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Reports;

/// <summary>
/// The logo files of the report templates, at <c>{StoragePath}/org-assets/{orgSlug}/report-logo-{templateId}-{unique}.{ext}</c>:
/// outside the public branding folder, so only the endpoints that check access send them. Every name that reaches the
/// disk has passed the rules of a slug and of a logo file name, so none can leave the folder. Each upload has a file
/// of its own: two uploads to one template never write the same file, so the one whose settings are not saved deletes
/// its file and never the one the settings name.
/// </summary>
public sealed partial class ReportLogoStore
{
    private const string FilePrefix = "report-logo-";

    private readonly IFileSystem _fs;
    private readonly ILogger<ReportLogoStore> _logger;
    private readonly string _root;

    public ReportLogoStore(IFileSystem fs, IOptions<StorageOptions> storage, ILogger<ReportLogoStore> logger)
    {
        _fs = fs;
        _logger = logger;
        _root = Path.GetFullPath(Path.Combine(storage.Value.StoragePath ?? ".", MagicStrings.OrganizationAssetsFolder));
    }

    // The template id, then the unique part of the upload; a name without it was stored before each upload had a
    // file of its own, and is still served and deleted
    [GeneratedRegex(@"^report-logo-[a-z0-9-]{1,32}(?:-[0-9a-f]{32})?\.(png|jpg|webp|svg)\z")]
    private static partial Regex FileNamePattern();

    /// <summary>A new name for a logo file of a template, <c>report-logo-{templateId}-{unique}.{extension}</c>: no other upload has it.</summary>
    public static string NewFileName(string templateId, string extension) =>
        $"{FilePrefix}{templateId}-{Guid.NewGuid():N}.{extension}";

    /// <summary>
    /// Stores <paramref name="logo"/> as a new file of the template and returns its name. The file is no one else's: it
    /// is not the logo of the template until the settings name it, so the caller deletes it (<see cref="Delete"/>) when
    /// they are not saved, and deletes the file they named before when they are. It is written whole beside its place
    /// and then renamed, so a reader never sees a part.
    /// </summary>
    public string Save(string orgSlug, string templateId, ReportLogo logo)
    {
        var fileName = NewFileName(templateId, logo.Extension);
        var path = PathOf(orgSlug, fileName);

        _fs.FolderCreate(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.tmp";
        try
        {
            _fs.WriteAllBytes(temporary, logo.Content);
            _fs.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (_fs.Exists(temporary))
                _fs.Delete(temporary);
        }

        return fileName;
    }

    /// <summary>The file of a logo, or null when it is not there. The name is one this store made.</summary>
    public string? Find(string orgSlug, string fileName)
    {
        var path = PathOf(orgSlug, fileName);
        return _fs.Exists(path) ? path : null;
    }

    /// <summary>Deletes a logo file (nothing when it is already gone). A failure is logged, never thrown: the settings no longer name the file.</summary>
    public void Delete(string orgSlug, string fileName)
    {
        var path = PathOf(orgSlug, fileName);
        Quietly(() =>
        {
            if (_fs.Exists(path))
                _fs.Delete(path);
        }, ex => _logger.LogWarning(ex, "The logo file {FileName} of organization {OrgSlug} could not be deleted",
            fileName, orgSlug));
    }

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

    /// <summary>Whether <paramref name="fileName"/> is the name of a logo file (what a template's <c>logo</c> holds).</summary>
    public static bool IsLogoFileName(string? fileName) => fileName != null && FileNamePattern().IsMatch(fileName);

    /// <summary>The media type of the logo file <paramref name="fileName"/>.</summary>
    public static string MediaTypeOf(string fileName) =>
        ReportLogo.MediaTypeOfExtension(Path.GetExtension(fileName).TrimStart('.'))
        ?? throw new ArgumentException("Not a logo file name", nameof(fileName));

    private string FolderOf(string orgSlug)
    {
        if (!SlugRules.IsValid(orgSlug))
            throw new ArgumentException("Not an organization slug", nameof(orgSlug));

        return Path.Combine(_root, orgSlug);
    }

    private string PathOf(string orgSlug, string fileName)
    {
        if (!IsLogoFileName(fileName))
            throw new ArgumentException("Not a logo file name", nameof(fileName));

        return Path.Combine(FolderOf(orgSlug), fileName);
    }
}
