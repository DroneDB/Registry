using System.Text.RegularExpressions;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
{
    public class WebUtils : IUtils
    {
        // Only lowercase letters, numbers, - and _. Max length 255
        private readonly Regex _organizationNameRegex = new Regex(@"^[a-z\d\-_]{1,255}$", RegexOptions.Compiled | RegexOptions.Singleline);
        public bool IsOrganizationNameValid(string name)
        {
            return _organizationNameRegex.IsMatch(name);
        }
    }
}
