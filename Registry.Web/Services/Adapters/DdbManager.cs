using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
{
    public class DdbManager : IDdbManager
    {
        private readonly ILogger<DdbManager> _logger;
        private readonly AppSettings _settings;

        public string DdbFolderName { get; } = ".ddb";

        public DdbManager(IOptions<AppSettings> settings, ILogger<DdbManager> logger)
        {
            _logger = logger;
            _settings = settings.Value;
        }

        public IDdb Get(string orgSlug, Guid internalRef)
        {
            var baseDdbPath = GetDdbPath(orgSlug, internalRef);

            Directory.CreateDirectory(baseDdbPath);

            var ddb = new Ddb(baseDdbPath);

            // TODO: It would be nice if we could use the bindings to check this
            if (!Directory.Exists(Path.Combine(baseDdbPath, DdbFolderName)))
            {

                ddb.Init();
                _logger.LogInformation($"Initialized new ddb in '{baseDdbPath}'");

            }
            else
                _logger.LogInformation($"Opened ddb in '{baseDdbPath}'");
            
            return ddb;
        }

        public void Delete(string orgSlug, Guid internalRef)
        {

            var baseDdbPath = GetDdbPath(orgSlug, internalRef);

            if (!Directory.Exists(baseDdbPath)) {
                _logger.LogWarning($"Asked to remove the folder '{baseDdbPath}' but it does not exist");
                return;
            }

            _logger.LogInformation($"Removing ddb '{baseDdbPath}'");

            Directory.Delete(baseDdbPath, true);

        }

        private string GetDdbPath(string orgSlug, Guid internalRef)
        {
            if (string.IsNullOrWhiteSpace(orgSlug))
                throw new ArgumentException("Organization slug cannot be null or empty");

            return Path.Combine(_settings.DdbStoragePath, orgSlug, internalRef.ToString());

        }
    }
}
