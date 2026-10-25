#nullable enable
using System;
using Registry.Web.Services.UpdateCheck;

namespace Registry.Web.Services.Ports;

/// <summary>
/// The update check's memory, in the storage folder every node shares. The check, the notification and the
/// information the Hub shows all go through it.
/// </summary>
public interface IUpdateCheckCache
{
    /// <summary>What is remembered; <see cref="UpdateCheckCache.Empty"/> when nothing is, or the file cannot be read.</summary>
    UpdateCheckCache Read();

    /// <summary>
    /// Replaces what is remembered with the result of <paramref name="change"/> (applied to the current content, under a
    /// lock of this process).
    /// </summary>
    void Update(Func<UpdateCheckCache, UpdateCheckCache> change);
}
