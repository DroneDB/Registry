using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
{
    public class PushManager : IPushManager
    {
        public Task<PushInitResultDto> Init(string orgSlug, string dsSlug, Stream stream)
        {
            throw new NotImplementedException();
        }
                
        public Task Upload(string orgSlug, string dsSlug, Stream stream)
        {
            throw new NotImplementedException();
        }

        public Task<object> Commit(string orgSlug, string dsSlug)
        {
            throw new NotImplementedException();
        }
    }
}
