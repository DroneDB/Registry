using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Services.Ports
{
    public interface IBatchTokenGenerator
    {
        public string GenerateToken();
    }
}
