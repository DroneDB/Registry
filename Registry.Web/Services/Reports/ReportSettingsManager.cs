using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;
using Registry.Web.Services.OrganizationAssets;
using Registry.Web.Services.Organizations;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Services.Reports;

/// <summary>A logo file ready to be sent: where it is and the media type it is served as.</summary>
public sealed record ReportLogoFile(string Path, string MediaType);

/// <summary>
/// The report templates and the annotation schema of the organizations. A reader of a dataset gets them (and the logos)
/// through the dataset; the members of the organization read them through the organization and its administrators
/// write them. An organization that wrote nothing has the default schema and the Standard template; its first write
/// (settings or logo) stores the Standard template as one of its own. Every write names the version it read and is
/// refused (409) when it is no longer the stored one, so two administrators never overwrite each other unseen.
/// </summary>
public sealed class ReportSettingsManager
{
    private readonly RegistryContext _context;
    private readonly IDatasetAccessResolver _access;
    private readonly IAuthManager _authManager;
    private readonly OrganizationRights _rights;
    private readonly ReportLogoStore _logos;
    private readonly OrganizationLogoLinks _organizationLogos;
    private readonly ILogger<ReportSettingsManager> _logger;

    public ReportSettingsManager(RegistryContext context, IDatasetAccessResolver access,
        IAuthManager authManager, OrganizationRights rights, ReportLogoStore logos,
        OrganizationLogoLinks organizationLogos, ILogger<ReportSettingsManager> logger)
    {
        _context = context;
        _access = access;
        _authManager = authManager;
        _rights = rights;
        _logos = logos;
        _organizationLogos = organizationLogos;
        _logger = logger;
    }

    // What an organization has now: its row, or the defaults (version 0) while it has none
    private sealed record Current(OrganizationReportSettings? Row, List<ReportTemplateDto> Templates, ReportSchemaDto Schema)
    {
        public int Version => Row?.Version ?? 0;
    }

    #region Reads

    /// <summary>The templates and the schema, for a reader of the dataset (read access to it).</summary>
    public async Task<ReportSettingsDto> GetForDataset(string orgSlug, string dsSlug, CancellationToken ct = default)
    {
        await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);
        var current = await LoadAsync(orgSlug, tracking: false, ct);
        return new ReportSettingsDto
        {
            Templates = current.Templates, Schema = current.Schema,
            OrganizationLogoUrl = _organizationLogos.DatasetUrlOf(orgSlug, dsSlug)
        };
    }

    /// <summary>The templates, the schema and the version, for the members of the organization.</summary>
    public async Task<OrganizationReportSettingsDto> GetForOrganization(string orgSlug, CancellationToken ct = default)
    {
        await _rights.RequireMemberAsync(orgSlug, ct);
        var current = await LoadAsync(orgSlug, tracking: false, ct);
        return Describe(orgSlug, current);
    }

    /// <summary>The logo of a template, for a reader of the dataset.</summary>
    /// <exception cref="NotFoundException">The template is not stored or has no logo.</exception>
    public async Task<ReportLogoFile> GetLogoForDataset(string orgSlug, string dsSlug, string templateId,
        CancellationToken ct = default)
    {
        await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);
        return LogoOf(orgSlug, await LoadAsync(orgSlug, tracking: false, ct), templateId);
    }

    /// <summary>The logo of a template, for the members of the organization.</summary>
    /// <exception cref="NotFoundException">The template is not stored or has no logo.</exception>
    public async Task<ReportLogoFile> GetLogoForOrganization(string orgSlug, string templateId,
        CancellationToken ct = default)
    {
        await _rights.RequireMemberAsync(orgSlug, ct);
        return LogoOf(orgSlug, await LoadAsync(orgSlug, tracking: false, ct), templateId);
    }

    #endregion

    #region Writes

    /// <summary>
    /// Replaces the templates and the schema (administrators of the organization). The logos stay with the templates
    /// that stay; the logo files of the templates that are no longer there are deleted.
    /// </summary>
    /// <exception cref="ForbiddenException">The current user does not administer the organization.</exception>
    /// <exception cref="BadRequestException">A rule of the settings is broken (<see cref="ReportSettingsValidator"/>).</exception>
    /// <exception cref="ConflictException">The version of the request is not the stored one.</exception>
    public async Task<OrganizationReportSettingsDto> Update(string orgSlug, ReportSettingsUpdateDto? update,
        CancellationToken ct = default)
    {
        await _rights.RequireAdministratorAsync(orgSlug, ct);

        var (templates, schema) = ReportSettingsValidator.Validate(update);
        var version = update!.Version ?? throw ReportSettingsValidator.Refuse("version", "version is required");

        var current = await LoadAsync(orgSlug, tracking: true, ct);
        RequireVersion(current, version);

        // The logo is not the writer's to set: each template that stays keeps the one it had
        var logos = current.Templates.ToDictionary(t => t.Id!, t => t.Logo);
        foreach (var template in templates)
            template.Logo = logos.GetValueOrDefault(template.Id!);

        var kept = templates.Select(t => t.Id).ToHashSet();
        var orphans = current.Templates.Where(t => t.Logo != null && !kept.Contains(t.Id)).Select(t => t.Logo!).ToList();

        var saved = await PersistAsync(orgSlug, current, templates, schema, "settings");
        foreach (var orphan in orphans)
            _logos.Delete(orgSlug, orphan);

        return Describe(orgSlug, saved);
    }

    /// <summary>
    /// Sets the logo of a stored template (administrators of the organization): the bytes are checked
    /// (<see cref="OrganizationImage.Inspect"/>), the version goes up.
    /// </summary>
    /// <exception cref="NotFoundException">The template is not stored (the Standard one counts as stored).</exception>
    public async Task<ReportLogoDto> SetLogo(string orgSlug, string templateId, byte[] content,
        CancellationToken ct = default)
    {
        await _rights.RequireAdministratorAsync(orgSlug, ct);

        var current = await LoadAsync(orgSlug, tracking: true, ct);
        var template = StoredTemplate(current, templateId);
        var logo = OrganizationImage.Inspect(content, ReportLogoStore.MaxBytes);

        // The file is this request's own (ReportLogoStore.Save): an upload that loses the race to another deletes only it,
        // never the file the winner's settings name
        var previous = template.Logo;
        var stored = _logos.Save(orgSlug, templateId, logo);
        template.Logo = stored;

        Current saved;
        try
        {
            saved = await PersistAsync(orgSlug, current, current.Templates, current.Schema, $"logo of template {templateId}");
        }
        catch
        {
            _logos.Delete(orgSlug, stored);
            throw;
        }

        // The settings name the new file: the one they named before is no one's
        if (previous != null)
            _logos.Delete(orgSlug, previous);

        return new ReportLogoDto { Logo = stored, Version = saved.Version };
    }

    /// <summary>Removes the logo of a stored template (administrators of the organization): the version goes up. A template with no logo is left as it is.</summary>
    /// <exception cref="NotFoundException">The template is not stored (the Standard one counts as stored).</exception>
    public async Task<ReportSettingsVersionDto> RemoveLogo(string orgSlug, string templateId,
        CancellationToken ct = default)
    {
        await _rights.RequireAdministratorAsync(orgSlug, ct);

        var current = await LoadAsync(orgSlug, tracking: true, ct);
        var template = StoredTemplate(current, templateId);

        var previous = template.Logo;
        if (previous == null)
            return new ReportSettingsVersionDto { Version = current.Version };

        template.Logo = null;
        var saved = await PersistAsync(orgSlug, current, current.Templates, current.Schema, $"logo of template {templateId}");
        _logos.Delete(orgSlug, previous);

        return new ReportSettingsVersionDto { Version = saved.Version };
    }

    #endregion

    #region Rules

    private static void RequireVersion(Current current, int version)
    {
        if (version != current.Version)
            throw new ConflictException(
                "The report settings were changed since they were loaded; load them again and repeat the change");
    }

    // A template the organization has stored: the Standard one counts while there is no row, as it is what the organization uses
    private static ReportTemplateDto StoredTemplate(Current current, string templateId) =>
        current.Templates.FirstOrDefault(t => t.Id == templateId)
        ?? throw new NotFoundException($"Template '{templateId}' was not found");

    private ReportLogoFile LogoOf(string orgSlug, Current current, string templateId)
    {
        var logo = StoredTemplate(current, templateId).Logo;
        if (!ReportLogoStore.IsLogoFileName(logo))
            throw new NotFoundException($"Template '{templateId}' has no logo");

        var path = _logos.Find(orgSlug, logo!)
                   ?? throw new NotFoundException($"Template '{templateId}' has no logo");
        return new ReportLogoFile(path, ReportLogoStore.MediaTypeOf(logo!));
    }

    #endregion

    #region Storage

    private async Task<Current> LoadAsync(string orgSlug, bool tracking, CancellationToken ct)
    {
        IQueryable<OrganizationReportSettings> rows = _context.OrganizationReportSettings;
        if (!tracking)
            rows = rows.AsNoTracking();

        var row = await rows.FirstOrDefaultAsync(r => r.OrganizationSlug == orgSlug, ct);
        return row == null
            ? new Current(null, ReportSettingsDefaults.Templates(), ReportSettingsDefaults.Schema())
            : new Current(row,
                ApiJson.Deserialize<List<ReportTemplateDto>>(row.Templates) ?? [],
                ApiJson.Deserialize<ReportSchemaDto>(row.Schema) ?? ReportSettingsDefaults.Schema());
    }

    // Writes the settings as the new version (the first write inserts the row) and logs it
    private async Task<Current> PersistAsync(string orgSlug, Current current, List<ReportTemplateDto> templates,
        ReportSchemaDto schema, string change)
    {
        var user = await _authManager.SafeGetCurrentUserName();
        var row = current.Row;
        var inserting = row == null;
        if (row == null)
        {
            row = new OrganizationReportSettings { OrganizationSlug = orgSlug };
            _context.OrganizationReportSettings.Add(row);
        }

        row.Templates = ApiJson.Serialize(templates);
        row.Schema = ApiJson.Serialize(schema);
        row.Version = current.Version + 1;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = user;

        try
        {
            await _context.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConflictException(
                "The report settings were changed while they were saved; load them again and repeat the change", ex);
        }
        catch (DbUpdateException ex) when (inserting)
        {
            // Another administrator wrote the first version at the same time (the row of the organization exists)
            _context.Entry(row).State = EntityState.Detached;
            throw new ConflictException(
                "The report settings were created while they were saved; load them again and repeat the change", ex);
        }

        _logger.LogInformation(
            "Report settings of organization {OrgSlug} updated by {UserName} to version {Version} ({Change})",
            orgSlug, user, row.Version, change);

        return new Current(row, templates, schema);
    }

    private OrganizationReportSettingsDto Describe(string orgSlug, Current current) =>
        new()
        {
            Templates = current.Templates, Schema = current.Schema, Version = current.Version,
            OrganizationLogoUrl = _organizationLogos.UrlOf(orgSlug)
        };

    #endregion
}
