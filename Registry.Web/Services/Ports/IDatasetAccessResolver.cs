#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Services.Managers;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Services.Ports;

/// <summary>
/// The one gate between a request and an organization or a dataset (RQ-06): it looks the resource up,
/// asks <see cref="IAuthManager"/> whether the current user may access it, and turns a refusal into a
/// <see cref="Exceptions.ForbiddenException"/> with the one message of the rule
/// (<see cref="Utilities.Auth.AccessDeniedMessages"/>; plan decision D2 makes it a 401 for an anonymous
/// caller and a 403 for a signed-in one). The access rules themselves stay in <see cref="IAuthManager"/>.
/// <para>A grant is remembered for the scope (the request): the OGC authorization filter, the MVT
/// endpoint and the managers of one request authorize a dataset once.</para>
/// </summary>
public interface IDatasetAccessResolver
{
    /// <summary>
    /// The dataset <paramref name="dsSlug"/> of <paramref name="orgSlug"/>, after checking that the current
    /// user has <paramref name="access"/> to it. With a <paramref name="path"/>, the result also carries
    /// the normalized dataset-relative path (<see cref="Utilities.DatasetPathPolicy.NormalizeRequestPath"/>:
    /// libddb's path rule, one leading '/' dropped) and the entry at that path, if any.
    /// </summary>
    /// <exception cref="Exceptions.BadRequestException">A slug is missing or invalid.</exception>
    /// <exception cref="Exceptions.NotFoundException">There is no such dataset.</exception>
    /// <exception cref="Exceptions.ForbiddenException">The current user may not access it.</exception>
    /// <exception cref="Exceptions.InvalidDatasetPathException">The path is not a dataset path (empty,
    /// NUL byte, absolute, "..", reserved).</exception>
    Task<DatasetAccess> ResolveAsync(string orgSlug, string dsSlug, AccessType access, string? path = null,
        CancellationToken ct = default);

    /// <summary>
    /// Checks that the current user has <paramref name="access"/> to a dataset the caller already holds
    /// (a tracked entity it is about to change, the dataset of a share batch).
    /// </summary>
    /// <exception cref="Exceptions.ForbiddenException">The current user may not access it.</exception>
    Task EnsureAccessAsync(Dataset dataset, AccessType access);

    /// <summary>
    /// The organization <paramref name="orgSlug"/>, after checking that the current user has
    /// <paramref name="access"/> to it. <paramref name="withTracking"/> and <paramref name="withDatasets"/>
    /// are those of <see cref="IUtils.GetOrganizationAsync"/>.
    /// </summary>
    /// <exception cref="Exceptions.BadRequestException">The slug is missing or invalid.</exception>
    /// <exception cref="Exceptions.NotFoundException">There is no such organization.</exception>
    /// <exception cref="Exceptions.ForbiddenException">The current user may not access it.</exception>
    Task<Organization> ResolveOrganizationAsync(string orgSlug, AccessType access, bool withTracking = false,
        bool withDatasets = false, CancellationToken ct = default);

    /// <summary>Checks that the current user has <paramref name="access"/> to an organization the caller holds.</summary>
    /// <exception cref="Exceptions.ForbiddenException">The current user may not access it.</exception>
    Task EnsureAccessAsync(Organization organization, AccessType access);
}

/// <summary>
/// A dataset the current user was granted access to (<see cref="IDatasetAccessResolver.ResolveAsync"/>).
/// </summary>
public sealed class DatasetAccess
{
    private readonly Lazy<IDDB> _ddb;

    public DatasetAccess(Dataset dataset, Func<IDDB> openDdb, string? path = null, Entry? entry = null)
    {
        Dataset = dataset;
        _ddb = new Lazy<IDDB>(openDdb);
        Path = path;
        Entry = entry;
    }

    /// <summary>The dataset, with its organization (untracked).</summary>
    public Dataset Dataset { get; }

    /// <summary>The dataset's organization and <c>InternalRef</c>: how a background job names it.</summary>
    public DatasetKey Key => new(Dataset.Organization.Slug, Dataset.InternalRef);

    /// <summary>The dataset's index, opened on first use: a caller that never reads the index does not
    /// open (or initialize) it.</summary>
    public IDDB Ddb => _ddb.Value;

    /// <summary>The normalized dataset-relative path; null when no path was resolved.</summary>
    public string? Path { get; }

    /// <summary>The entry at <see cref="Path"/>; null when no path was resolved or nothing is there (the
    /// caller decides whether that is a 400 or a 404).</summary>
    public Entry? Entry { get; }
}
