#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using Registry.Ports;
using Registry.Web.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Keeps the push session as a JSON file in the push folder, and the upload verifications (XI-05)
/// as one small record per verified file under <c>verified/</c>, next to it. A loaded session is
/// cached per process while its file keeps its size and write time: every upload of a push reads
/// it (the delta can list thousands of files).
/// </summary>
public class PushSessionStore : IPushSessionStore
{
    private const string SessionFileName = "session.json";
    private const string VerifiedFolderName = "verified";
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    private readonly IFileSystem _fileSystem;
    private readonly IMemoryCache? _cache;

    public PushSessionStore(IFileSystem fileSystem, IMemoryCache? cache = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _cache = cache;
    }

    public Task SaveAsync(string pushFolder, PushSession session) =>
        _fileSystem.WriteAllTextAsync(SessionPath(pushFolder), JsonConvert.SerializeObject(session));

    public async Task<PushSession?> LoadAsync(string pushFolder)
    {
        var path = SessionPath(pushFolder);
        if (!_fileSystem.Exists(path))
            return null;

        // Valid while the file is the one that was read
        var version = (_fileSystem.GetFileSize(path), _fileSystem.GetLastWriteTime(path).ToUniversalTime());
        var key = "push-session:" + Path.GetFullPath(path);
        if (_cache != null && _cache.TryGetValue(key, out CachedSession? cached) && cached!.Version == version)
            return cached.Session;

        var session = JsonConvert.DeserializeObject<PushSession>(await _fileSystem.ReadAllTextAsync(path));
        if (session != null)
            _cache?.Set(key, new CachedSession(version, session), new MemoryCacheEntryOptions { SlidingExpiration = CacheFor });
        return session;
    }

    public bool Exists(string pushFolder) => _fileSystem.Exists(SessionPath(pushFolder));

    public async Task MarkVerifiedAsync(string pushFolder, string path, string sha256, string localFile)
    {
        var record = new Verification(sha256, _fileSystem.GetFileSize(localFile),
            _fileSystem.GetLastWriteTime(localFile).ToUniversalTime().Ticks);
        var marker = MarkerPath(pushFolder, path);
        _fileSystem.EnsureParentFolderExists(marker);
        await _fileSystem.WriteAllTextAsync(marker, JsonConvert.SerializeObject(record));
    }

    public async Task<IReadOnlyDictionary<string, string>> VerifiedHashesAsync(string pushFolder, string addFolder,
        PushSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var verified = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var add in session.Delta.Adds)
        {
            if (add.Hash.Length == 0 || verified.ContainsKey(add.Path))
                continue;

            var marker = MarkerPath(pushFolder, add.Path);
            var file = DatasetPathPolicy.ToLocalPath(addFolder, add.Path);
            if (!_fileSystem.Exists(marker) || !_fileSystem.Exists(file))
                continue;

            Verification? record;
            try
            {
                record = JsonConvert.DeserializeObject<Verification>(await _fileSystem.ReadAllTextAsync(marker));
            }
            catch (JsonException)
            {
                continue;
            }

            // The file the upload verified, unchanged since, with the delta's own hash
            if (record != null &&
                string.Equals(record.Sha256, add.Hash, StringComparison.Ordinal) &&
                record.Size == _fileSystem.GetFileSize(file) &&
                record.ModifiedUtcTicks == _fileSystem.GetLastWriteTime(file).ToUniversalTime().Ticks)
                verified[add.Path] = add.Hash;
        }

        return verified;
    }

    private static string SessionPath(string pushFolder) => Path.Combine(pushFolder, SessionFileName);

    private static string MarkerPath(string pushFolder, string path) =>
        DatasetPathPolicy.ToLocalPath(Path.Combine(pushFolder, VerifiedFolderName), path);

    private sealed record CachedSession((long Size, DateTime ModifiedUtc) Version, PushSession Session);

    private sealed record Verification(string Sha256, long Size, long ModifiedUtcTicks);
}
