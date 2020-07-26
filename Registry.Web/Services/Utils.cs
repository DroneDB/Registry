using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Registry.Web.Services
{
    public class Utils : IUtils
    {
        // Only lowercase letters, numbers, - and _. Max length 255
        private readonly Regex _organizationNameRegex = new Regex(@"^[a-z\d\-_]{1,255}$", RegexOptions.Compiled | RegexOptions.Singleline);
        public bool IsOrganizationNameValid(string name)
        {
            return _organizationNameRegex.IsMatch(name);
        }
    }
}
