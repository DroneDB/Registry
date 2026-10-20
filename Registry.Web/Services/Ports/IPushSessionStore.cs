#nullable enable
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
}
