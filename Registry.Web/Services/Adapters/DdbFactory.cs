using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Registry.Web.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
{
    public class DdbFactory : IDdbFactory
    {
        private readonly AppSettings _settings;

        public DdbFactory(IOptions<AppSettings> settings)
        {
            _settings = settings.Value;
        }

        // NOTE: This logic is separated from the managers classes because it is used in multiple places and it could be subject to change
        
        public IDdb GetDdb(string orgId, string dsId)
        {
            var ddbPath = Path.Combine(_settings.DdbStoragePath, $"{orgId}-{dsId}", ".ddb");

            var res = new Ddb(ddbPath);
            res.Database.EnsureCreated();

            return res;
        }
    }
}
