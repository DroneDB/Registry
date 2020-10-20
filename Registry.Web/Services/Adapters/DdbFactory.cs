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
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
{
    public class DdbFactory : IDdbFactory
    {
        private readonly ILogger<DdbFactory> _logger;
        private readonly AppSettings _settings;

        public DdbFactory(IOptions<AppSettings> settings, ILogger<DdbFactory> logger)
        {
            _logger = logger;
            _settings = settings.Value;
        }

        // NOTE: This logic is separated from the manager classes because it is used in multiple places and it could be subject to change

        public IDdb GetDdb(string orgSlug, string dsSlug)
        {
            var baseDdbPath = Path.Combine(_settings.DdbStoragePath, orgSlug, dsSlug);
            
            _logger.LogInformation($"Opening ddb in '{baseDdbPath}'");

            var ddb = new Ddb(baseDdbPath);
            
            

            return ddb;
        }


    }
}
