using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Factory for IDDB instances; resolves dataset folder paths and creates DDB wrappers.
/// </summary>
public class DdbManager : IDdbManager
{
    private readonly ILogger<DdbManager> _logger;
    private readonly IDdbWrapper _ddbWrapper;
    private readonly AppSettings _settings;

    public DdbManager(IOptions<AppSettings> settings, ILogger<DdbManager> logger, IDdbWrapper ddbWrapper)
    {
        _logger = logger;
        _ddbWrapper = ddbWrapper;
        _settings = settings.Value;
    }

    public IDDB Get(string orgSlug, Guid internalRef)
    {
        var baseDdbPath = GetDdbPath(orgSlug, internalRef);

        Directory.CreateDirectory(baseDdbPath);

        var ddb = new DDB(baseDdbPath, _ddbWrapper);

        // TODO: It would be nice if we could use the bindings to check this
        if (!Directory.Exists(Path.Combine(baseDdbPath, IDDB.DatabaseFolderName)))
        {
            ddb.Init();
            _logger.LogInformation("Initialized new ddb in '{DaseDdbPath}'", baseDdbPath);
        }
        else
            _logger.LogDebug("Opened ddb in '{BaseDdbPath}'", baseDdbPath);

        return ddb;
    }

    public void Delete(string orgSlug, Guid internalRef)
    {

        var baseDdbPath = GetDdbPath(orgSlug, internalRef);

        if (!Directory.Exists(baseDdbPath)) {
            _logger.LogWarning("Asked to remove the folder '{BaseDdbPath}' but it does not exist", baseDdbPath);
            return;
        }

        _logger.LogInformation("Removing ddb dataset '{BaseDdbPath}'", baseDdbPath);

        ReleaseConnections(baseDdbPath);
        Directory.Delete(baseDdbPath, true);

    }

    public void Delete(string orgSlug)
    {
        var orgPath = Path.Combine(_settings.DatasetsPath, orgSlug);

        if (!Directory.Exists(orgPath)) {
            _logger.LogWarning("Asked to remove the folder '{OrgPath}' but it does not exist", orgPath);
            return;
        }

        _logger.LogInformation("Removing ddb '{OrgPath}'", orgPath);

        ReleaseConnections(orgPath);
        Directory.Delete(orgPath, true);
    }

    public void ReleaseConnections(string folder)
    {
        // Best effort: the delete, move or wipe that follows must not fail because the index is
        // busy or libddb predates DDBReleaseConnections (only Windows refuses open files anyway)
        try
        {
            var closed = _ddbWrapper.ReleaseConnections(folder);
            if (closed > 0)
                _logger.LogDebug("Closed {Count} idle index connections under '{Folder}'", closed, folder);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot release the index connections under '{Folder}'", folder);
        }
    }

    private string GetDdbPath(string orgSlug, Guid internalRef)
    {
        if (string.IsNullOrWhiteSpace(orgSlug))
            throw new ArgumentException("Organization slug cannot be null or empty");

        return Path.Combine(_settings.DatasetsPath, orgSlug, internalRef.ToString());

    }
}