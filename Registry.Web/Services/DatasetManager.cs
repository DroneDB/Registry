using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports.ObjectSystem;
using Registry.Web.Data;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services
{
    public class DatasetManager : IDatasetManager
    {
        private readonly RegistryContext _context;
        private readonly ILogger<DatasetManager> _logger;
        private readonly IObjectSystem _objectSystem;

        public DatasetManager(RegistryContext context, ILogger<DatasetManager> logger, IObjectSystem objectSystem)
        {
            _context = context;
            _logger = logger;
            _objectSystem = objectSystem;
        }

        public void CreateDataset(DatasetDto ds)
        {
            // TODO: Implement
        }

        public void RemoveDataset(int id)
        {
            // TODO: Implement
        }
    }
}
