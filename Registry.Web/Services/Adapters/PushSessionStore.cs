#nullable enable
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Registry.Ports;
using Registry.Web.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Keeps the push session as a JSON file in the push folder.
/// </summary>
public class PushSessionStore : IPushSessionStore
{
    private const string SessionFileName = "session.json";

    private readonly IFileSystem _fileSystem;

    public PushSessionStore(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public Task SaveAsync(string pushFolder, PushSession session) =>
        _fileSystem.WriteAllTextAsync(SessionPath(pushFolder), JsonConvert.SerializeObject(session));

    public async Task<PushSession?> LoadAsync(string pushFolder)
    {
        var path = SessionPath(pushFolder);
        if (!_fileSystem.Exists(path))
            return null;

        return JsonConvert.DeserializeObject<PushSession>(await _fileSystem.ReadAllTextAsync(path));
    }

    public bool Exists(string pushFolder) => _fileSystem.Exists(SessionPath(pushFolder));

    private static string SessionPath(string pushFolder) => Path.Combine(pushFolder, SessionFileName);
}
