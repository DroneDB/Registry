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

        private const string WindowsExeName = "ddbcmd.exe";
        private const string LinuxExeName = "ddb";


        public DdbFactory(IOptions<AppSettings> settings, ILogger<DdbFactory> logger)
        {
            _logger = logger;
            _settings = settings.Value;
        }

        // NOTE: This logic is separated from the managers classes because it is used in multiple places and it could be subject to change

        public IDdb GetDdb(string orgId, string dsId)
        {
            var ddbPath = Path.Combine(_settings.DdbStoragePath, orgId, dsId, DefaultDdbFolder, DefaultDdbSqliteName);

            _logger.LogInformation($"Opening ddb in '{ddbPath}'");
            
            var ddb = new Ddb(ddbPath, CalculateExePath());

            var res = ddb.Database.EnsureCreated();

            _logger.LogInformation(res ? "Database created" : "Database already existing");

            return ddb;
        }

        private string CalculateExePath()
        {
            var winPath = Path.Combine(_settings.DdbPath, WindowsExeName);

            if (File.Exists(winPath))
                return winPath;

            var linuxPath = Path.Combine(_settings.DdbPath, LinuxExeName);

            if (File.Exists(linuxPath))
                return linuxPath;

            throw new ArgumentException("Cannot find DDB executable in path");

        }
    }
}
