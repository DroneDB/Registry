#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Registry.Web.Models;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Persists the <see cref="PushSession"/> of a push in its push folder.
/// </summary>
public interface IPushSessionStore
{
    Task SaveAsync(string pushFolder, PushSession session);

    /// <summary>The session of the push folder, or null when it has none.</summary>
    Task<PushSession?> LoadAsync(string pushFolder);

    /// <summary>Whether the push folder holds a session (the push was initialized).</summary>
    bool Exists(string pushFolder);

    /// <summary>
    /// Records that the file of <paramref name="path"/> in the push folder was checked against its
    /// delta hash while it was received (XI-05): its hash, with the size and write time it has now.
    /// </summary>
    Task MarkVerifiedAsync(string pushFolder, string path, string sha256, string localFile);

    /// <summary>
    /// The files of <paramref name="session"/>'s delta found in <paramref name="addFolder"/> that were
    /// verified (<see cref="MarkVerifiedAsync"/>) and still have that size and write time: delta path →
    /// lowercase hex SHA-256 (the delta's). A file staged any other way (a hard-linked local file, a
    /// push started before verification existed) is left out, so libddb hashes it.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> VerifiedHashesAsync(string pushFolder, string addFolder,
        PushSession session);
}
