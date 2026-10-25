using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using DdbException = Registry.Ports.DroneDB.DdbException;
using DdbErrors = Registry.Ports.DroneDB.DdbErrors;
using IDDB = Registry.Ports.DroneDB.IDDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Key-value metadata CRUD for datasets via DroneDB native calls.
/// </summary>
public class MetaManager : IMetaManager
{
    private readonly ILogger<MetaManager> _logger;
    private readonly IDatasetAccessResolver _access;
    private readonly IDatasetListingInvalidator _listings;

    public MetaManager(ILogger<MetaManager> logger, IDatasetAccessResolver access,
        IDatasetListingInvalidator listings)
    {
        _logger = logger;
        _access = access;
        _listings = listings;
    }

    /// <summary>
    /// The dataset's visibility or name (root meta) may decide who lists it and how: their caches go
    /// (<see cref="IDatasetListingInvalidator"/>).
    /// </summary>
    private async Task OnDatasetMetaChanged(Data.Models.Dataset ds, string key, string path)
    {
        if (key == SafeMetaManager.VisibilityField)
        {
            await _listings.VisibilityChangedAsync(ds.Organization.Slug, ds.InternalRef);
            _logger.LogDebug("Invalidated the visibility and listings of {OrgSlug}/{DsSlug}", ds.Organization.Slug, ds.Slug);
        }
        else if (key == SafeMetaManager.NameField && path == null)
        {
            await _listings.ListingChangedAsync(ds.Organization.Slug, ds.InternalRef);
        }
    }

    public async Task<MetaDto> Add(string orgSlug, string dsSlug, string key, string data, string path = null, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);

        _logger.LogInformation("In Add('{OrgSlug}/{DsSlug}', {Key}, {Path})", orgSlug, dsSlug, key, path);

        RequireWritableKey(key, data, MetaKeyKind.List);

        var ddb = access.Ddb;

        if (path != null && !ddb.EntryExists(path))
            throw new BadRequestException($"Path '{path}' does not exist");

        return ddb.Meta.Add(key, data, path).ToDto();
    }

    public async Task<MetaDto> Set(string orgSlug, string dsSlug, string key, string data, string path = null, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);
        var ds = access.Dataset;

        _logger.LogInformation("In Set('{OrgSlug}/{DsSlug}', {Key}, {Path})", orgSlug, dsSlug, key, path);

        RequireWritableKey(key, data, MetaKeyKind.Item);

        var ddb = access.Ddb;

        if (path != null && !ddb.EntryExists(path))
            throw new BadRequestException($"Path '{path}' does not exist");

        var result = ddb.Meta.Set(key, data, path).ToDto();
        await OnDatasetMetaChanged(ds, key, path);
        return result;
    }

    public async Task<int> Remove(string orgSlug, string dsSlug, string id, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);

        _logger.LogInformation("In Remove('{OrgSlug}/{DsSlug}', {Id})", orgSlug, dsSlug, id);

        if (string.IsNullOrWhiteSpace(id))
            throw new BadRequestException("Id should not be null or empty");

        var ddb = access.Ddb;

        return ddb.Meta.Remove(id);
    }

    public async Task<JsonElement> Get(string orgSlug, string dsSlug, string key, string path = null, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);

        _logger.LogInformation("In Get('{OrgSlug}/{DsSlug}', {Key}, {Path})", orgSlug, dsSlug, key, path);

        if (string.IsNullOrWhiteSpace(key))
            throw new BadRequestException("Key should not be null or empty");

        var ddb = access.Ddb;

        if (path != null && !ddb.EntryExists(path))
            throw new BadRequestException($"Path '{path}' does not exist");

        try
        {
            return ddb.Meta.Get(key, path);
        }
        catch (DdbException ex) when (!DdbErrors.IsTransient(ex) && !HasKey(ddb, key, path))
        {
            // libddb answers a key nobody wrote with a generic failure: the dataset's own list of keys says
            // whether that is the reason, so the message is never parsed and any other failure stays one
            throw new NotFoundException($"No metadata found for key {key}" + (path == null ? "" : $" and path {path}"), ex);
        }
    }

    /// <summary>
    /// The checks every write has in common: a key and data are given, and the key follows libddb's rule for
    /// <paramref name="kind"/> (<see cref="MetaKeyRule"/>: a refused key is a 400 with the rule's message, not a
    /// 500 from the native call).
    /// </summary>
    private static void RequireWritableKey(string key, string data, MetaKeyKind kind)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new BadRequestException("Key should not be null or empty");

        if (data == null)
            throw new BadRequestException("Data should not be null");

        if (MetaKeyRule.Violation(key, kind) is { } violation)
            throw new BadRequestException(violation, noRetry: true);
    }

    private static bool HasKey(IDDB ddb, string key, string path) =>
        ddb.Meta.List(path).Any(item => item.Key == key && item.Path == (path ?? ""));

    public async Task<int> Unset(string orgSlug, string dsSlug, string key, string path = null, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);
        var ds = access.Dataset;

        _logger.LogInformation("In Unset('{OrgSlug}/{DsSlug}', {Key}, {Path})", orgSlug, dsSlug, key, path);

        if (string.IsNullOrWhiteSpace(key))
            throw new BadRequestException("Key should not be null or empty");

        var ddb = access.Ddb;

        if (path != null && !ddb.EntryExists(path))
            throw new BadRequestException($"Path '{path}' does not exist");

        var result = ddb.Meta.Unset(key, path);
        await OnDatasetMetaChanged(ds, key, path);
        return result;

    }

    public async Task<IEnumerable<MetaListItemDto>> List(string orgSlug, string dsSlug, string path = null, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);

        _logger.LogInformation("In List('{OrgSlug}/{DsSlug}', {Path})", orgSlug, dsSlug, path);

        var ddb = access.Ddb;

        if (path != null && !ddb.EntryExists(path))
            throw new BadRequestException($"Path '{path}' does not exist");

        return ddb.Meta.List(path).Select(item => item.ToDto());
    }

    public async Task<IEnumerable<MetaDumpDto>> Dump(string orgSlug, string dsSlug, string ids = null, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);

        _logger.LogInformation("In Dump('{OrgSlug}/{DsSlug}', {Ids})", orgSlug, dsSlug, ids);

        var ddb = access.Ddb;

        return ddb.Meta.Dump(ids).Select(item => item.ToDto());
    }
}