using System;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Bytes reserved for one upload. Dispose without <see cref="Commit"/> to give them back.
/// </summary>
public interface IQuotaReservation : IDisposable
{
    /// <summary>Changes the reservation to the actual size (e.g. once the file is on disk).</summary>
    /// <exception cref="Registry.Web.Exceptions.QuotaExceededException">The new size does not fit.</exception>
    void Resize(long bytes);

    /// <summary>The upload is stored: its bytes count as used until the next evaluation.</summary>
    void Commit();

    /// <summary>
    /// Moves <paramref name="bytes"/> of this reservation into a new one, which is committed or released on its own
    /// (the files whose fate is known later than the others'); the total reserved does not change.
    /// </summary>
    /// <exception cref="System.ArgumentOutOfRangeException">More than this reservation holds, or negative.</exception>
    IQuotaReservation Split(long bytes);
}
