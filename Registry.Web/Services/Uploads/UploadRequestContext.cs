using System;
using System.Collections.Generic;
using System.IO;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Uploads;

/// <summary>
/// What an upload request resolved once, before its body: the dataset, its index,
/// the user, the quota reservation and the index-queue registration. Disposing it gives back
/// what was not used: the reservation, the registration and temp files never committed.
/// </summary>
public sealed class UploadRequestContext : IDisposable
{
    private readonly List<string> _tempFiles = [];

    internal UploadRequestContext(string orgSlug, string dsSlug, Dataset dataset, IDDB ddb, User? user,
        string? uploadId)
    {
        OrgSlug = orgSlug;
        DsSlug = dsSlug;
        Dataset = dataset;
        Ddb = ddb;
        User = user;
        UploadId = uploadId;
    }

    public string OrgSlug { get; }
    public string DsSlug { get; }
    public Dataset Dataset { get; }
    public IDDB Ddb { get; }
    public User? User { get; }

    /// <summary>The client's <c>X-Upload-Id</c>, or null.</summary>
    public string? UploadId { get; }

    /// <summary>The receipt of an upload with the same id that is already done, or null.</summary>
    public UploadReceipt? Receipt { get; internal set; }

    public DatasetKey Key => new(OrgSlug, Dataset.InternalRef);

    /// <summary>The id the quota ledger knows the user by: empty for an anonymous caller.</summary>
    internal string QuotaUserId => User?.Id ?? string.Empty;

    internal IQuotaReservation? Reservation { get; set; }
    internal UploadTicket? Ticket { get; set; }

    internal void TrackTempFile(string path)
    {
        lock (_tempFiles) _tempFiles.Add(path);
    }

    public void Dispose()
    {
        Reservation?.Dispose();
        Ticket?.Dispose();

        string[] temps;
        lock (_tempFiles) temps = _tempFiles.ToArray();
        foreach (var temp in temps)
        {
            // Committed files were moved away; anything left was never committed
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                /* the abandoned-upload sweep removes it */
            }
        }
    }
}
