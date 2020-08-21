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
        private const string DefaultDdbFolder = ".ddb";
        private const string DefaultDdbSqliteName = "dbase.sqlite";

        public DdbFactory(IOptions<AppSettings> settings, ILogger<DdbFactory> logger)
        {
            _logger = logger;
            _settings = settings.Value;
        }

        // NOTE: This logic is separated from the manager classes because it is used in multiple places and it could be subject to change

        public IDdbStorage GetDdb(string orgId, string dsId)
        {
            var ddbPath = Path.Combine(_settings.DdbStoragePath, orgId, dsId, DefaultDdbFolder, DefaultDdbSqliteName);
            
            IDdb ddb = new Ddb(Path.Combine(_settings.DdbPath, DdbPackageProvider.GetExeName()));
            
            if (!File.Exists(ddbPath))
            {
                _logger.LogInformation($"Database not found, creating new in '{ddbPath}'");
                ddb.CreateDatabase(ddbPath);
                _logger.LogInformation("Empty database created");
            }

            _logger.LogInformation($"Opening ddb in '{ddbPath}'");

            var ddbStorage = new DdbStorage(ddbPath, ddb);

            var res = ddbStorage.Database.EnsureCreated();

            _logger.LogInformation(res ? "Database created" : "Database already existing");

            return ddbStorage;
        }

    }
}
