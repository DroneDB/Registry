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

        public IDdb GetDdb(string orgSlug, Guid internalRef)
        {
            var baseDdbPath = Path.Combine(_settings.DdbStoragePath, orgSlug, internalRef.ToString());
            
            _logger.LogInformation($"Opening ddb in '{baseDdbPath}'");
          
            return new Ddb(baseDdbPath);
        }


    }
}
