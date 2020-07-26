using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Services
{
    public interface IUtils
    {
        bool IsOrganizationNameValid(string name);
    }
}
