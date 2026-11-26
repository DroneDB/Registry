using System;
using System.IO;
using System.Text.RegularExpressions;
using Registry.Web.Services.OrganizationAssets;

namespace Registry.Web.Services.Reports;

/// <summary>
/// The logo files of the report templates, at <c>{StoragePath}/org-assets/{orgSlug}/report-logo-{templateId}-{unique}.{ext}</c>
/// (<see cref="OrganizationAssetStore"/> keeps them). It owns the names: every one has the shape of a logo file name,
/// and each upload has a file of its own, so two uploads to one template never write the same file, and the one whose
/// settings are not saved deletes its file and never the one the settings name.
/// </summary>
public sealed partial class ReportLogoStore
{
    /// <summary>The largest report logo accepted, in bytes.</summary>
    public const int MaxBytes = 512 * 1024;

    private const string FilePrefix = "report-logo-";

    private readonly OrganizationAssetStore _assets;

    public ReportLogoStore(OrganizationAssetStore assets)
    {
        _assets = assets;
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
    /// they are not saved, and deletes the file they named before when they are.
    /// </summary>
    public string Save(string orgSlug, string templateId, OrganizationImage logo)
    {
        var fileName = NewFileName(templateId, logo.Extension);
        _assets.Save(orgSlug, fileName, logo.Content, replace: false);
        return fileName;
    }

    /// <summary>The file of a logo, or null when it is not there. The name is one this store made.</summary>
    public string? Find(string orgSlug, string fileName) => _assets.Find(orgSlug, Checked(fileName));

    /// <summary>Deletes a logo file (nothing when it is already gone). A failure is logged, never thrown: the settings no longer name the file.</summary>
    public void Delete(string orgSlug, string fileName) => _assets.Delete(orgSlug, Checked(fileName));

    /// <summary>Whether <paramref name="fileName"/> is the name of a logo file (what a template's <c>logo</c> holds).</summary>
    public static bool IsLogoFileName(string? fileName) => fileName != null && FileNamePattern().IsMatch(fileName);

    /// <summary>The media type of the logo file <paramref name="fileName"/>.</summary>
    public static string MediaTypeOf(string fileName) =>
        OrganizationImage.MediaTypeOfExtension(Path.GetExtension(fileName).TrimStart('.'))
        ?? throw new ArgumentException("Not a logo file name", nameof(fileName));

    private static string Checked(string fileName) =>
        IsLogoFileName(fileName) ? fileName : throw new ArgumentException("Not a logo file name", nameof(fileName));
}
