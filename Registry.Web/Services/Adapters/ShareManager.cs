using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
{
    public class ShareManager : IShareManager
    {

        private IOrganizationsManager _organizationsManager;
        private IDatasetsManager _datasetsManager;
        private IObjectsManager _objectsManager;
        private ILogger<ShareManager> _logger;

        public ShareManager(ILogger<ShareManager> logger, IObjectsManager objectsManager, IDatasetsManager datasetsManager, IOrganizationsManager organizationsManager)
        {
            _logger = logger;
            _objectsManager = objectsManager;
            _datasetsManager = datasetsManager;
            _organizationsManager = organizationsManager;
        }

        public async Task<string> Initialize(ShareInitDto parameters)
        {
            
            if (parameters?.Dataset == null || parameters.Organization == null)
                throw new BadRequestException("Invalid parameters");


            throw new NotImplementedException();
        }

        public async Task Upload(string token, string path, byte[] data)
        {
            throw new NotImplementedException();
        }

        public async Task Commit(string token)
        {
            throw new NotImplementedException();
        }
    }
}
