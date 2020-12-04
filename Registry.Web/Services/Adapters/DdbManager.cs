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

        public DdbManager(IOptions<AppSettings> settings, ILogger<DdbManager> logger)
        {
            _logger = logger;
            _settings = settings.Value;
        }

        public IDdb Get(string orgSlug, string dsSlug)
        {
            var baseDdbPath = GetDdbPath(orgSlug, dsSlug);

            Directory.CreateDirectory(baseDdbPath);

            var ddb = new Ddb(baseDdbPath);

            // TODO: It would be nice if we could use the bindings to check this
            if (!Directory.Exists(Path.Combine(baseDdbPath, ".ddb")))
            {

                ddb.Init();
                _logger.LogInformation($"Initialized new ddb in '{baseDdbPath}'");

            }
            else
                _logger.LogInformation($"Opened ddb in '{baseDdbPath}'");
            
            return ddb;
        }

        public void Move(string orgSlug, string dsSlug, string newDsSlug)
        {

            var baseDdbPath = GetDdbPath(orgSlug, dsSlug);
            var newDdbPath = GetDdbPath(orgSlug, newDsSlug);

            if (!Directory.Exists(baseDdbPath))
                throw new ArgumentException($"Cannot move ddb from '{newDdbPath}': source directory does not exist");

            if (Directory.Exists(newDdbPath))
                throw new ArgumentException($"Cannot move ddb to '{newDdbPath}': directory already exists");

            _logger.LogInformation($"Moving ddb from '{baseDdbPath}' to '{newDdbPath}'");

            Directory.Move(baseDdbPath, newDdbPath);

        }

        public void Delete(string orgSlug, string dsSlug)
        {

            var baseDdbPath = GetDdbPath(orgSlug, dsSlug);

            if (!Directory.Exists(baseDdbPath)) {
                _logger.LogWarning($"Asked to remove the folder '{baseDdbPath}' but it does not exist");
                return;
            }

            _logger.LogInformation($"Removing ddb '{baseDdbPath}'");

            Directory.Delete(baseDdbPath, true);

        }

        private string GetDdbPath(string orgSlug, string dsSlug)
        {
            if (string.IsNullOrWhiteSpace(orgSlug))
                throw new ArgumentException("Organization slug cannot be null or empty");

            if (string.IsNullOrWhiteSpace(dsSlug))
                throw new ArgumentException("Dataset slug cannot be null or empty");

            return Path.Combine(_settings.DdbStoragePath, orgSlug, dsSlug);

        }
    }
}
