using Microsoft.Extensions.Logging;
using Registry.Ports.ObjectSystem;
using Registry.Web.Data;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
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
