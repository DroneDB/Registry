#nullable enable
using System;
using System.Threading.Tasks;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Storage-quota admission for uploads (UPL-01 E.2, C.6). The user's storage use is evaluated
/// once per window and every upload reserves its size against it, so a burst of uploads costs one
/// evaluation instead of one per file, and an upload over quota is refused before its first byte.
/// </summary>
public interface IUploadQuotaGuard
{
    /// <summary>
    /// Reserves <paramref name="bytes"/> for an upload of the current user.
    /// </summary>
    /// <exception cref="Registry.Web.Exceptions.QuotaExceededException">The upload does not fit.</exception>
    Task<IQuotaReservation> ReserveAsync(string userId, long bytes);
}

/// <summary>
/// Bytes reserved for one upload. Dispose without <see cref="Commit"/> to give them back.
/// </summary>
public interface IQuotaReservation : IDisposable
{
    /// <summary>Bytes reserved.</summary>
    long Bytes { get; }

    /// <summary>Changes the reservation to the actual size (e.g. once the file is on disk).</summary>
    /// <exception cref="Registry.Web.Exceptions.QuotaExceededException">The new size does not fit.</exception>
    void Resize(long bytes);

    /// <summary>The upload is stored: its bytes count as used until the next evaluation.</summary>
    void Commit();
}
