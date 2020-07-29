using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports.ObjectSystem;
using Registry.Web.Data;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
{
    public class ObjectsManager : IObjectsManager
    {
        private readonly ILogger<ObjectsManager> _logger;
        private readonly IObjectSystem _objectSystem;
        private readonly RegistryContext _context;

        public ObjectsManager(ILogger<ObjectsManager> logger, IObjectSystem objectSystem, RegistryContext context)
        {
            _logger = logger;
            _objectSystem = objectSystem;
            _context = context;
        }

        public Task<IEnumerable<ObjectDto>> Get(string orgId, string dsId, string path)
        {
            throw new NotImplementedException();
        }
    }
}
