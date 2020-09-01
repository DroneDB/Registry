using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
{

    // NOTE: This class is a fundamental piece of the architecture because 
    // it encapsulates all the validation logic of the organizations and datasets
    // The logic is centralized here because it could be subject to change

    public class WebUtils : IUtils
    {
        private readonly IAuthManager _authManager;
        private readonly RegistryContext _context;

        public WebUtils(IAuthManager authManager, RegistryContext context)
        {
            _authManager = authManager;
            _context = context;
        }

        // Only lowercase letters, numbers, - and _. Max length 255
        private readonly Regex _safeNameRegex = new Regex(@"^[a-z\d\-_]{1,255}$", RegexOptions.Compiled | RegexOptions.Singleline);
        public bool IsSlugValid(string name)
        {
            return _safeNameRegex.IsMatch(name);
        }

        // Fast and dirty
        public string MakeSlug(string name)
        {
            // NOTE: Needs to call Encoding.RegisterProvider(CodePagesEncodingProvider.Instance) of System.Text.Encoding.CodePages nuget package
            var tempBytes = Encoding.GetEncoding("ISO-8859-8").GetBytes(name);
            var tmp = Encoding.UTF8.GetString(tempBytes);

            var res = new string(tmp.Select(c => char.IsSeparator(c) ? '-' : c).ToArray());

            return res.ToLowerInvariant();
        }


        public async Task<Organization> GetOrganizationAndCheck(string orgSlug, bool safe = false)
        {

            if (string.IsNullOrWhiteSpace(orgSlug))
                throw new BadRequestException("Missing organization id");

            if (!IsSlugValid(orgSlug))
                throw new BadRequestException("Invalid organization id");
            
            var org = _context.Organizations.Include(item => item.Datasets).FirstOrDefault(item => item.Slug == orgSlug);

            if (org == null)
            {
                if (safe) return null;

                throw new NotFoundException("Organization not found");
            }

            if (!await _authManager.IsUserAdmin())
            {
                var currentUser = await _authManager.GetCurrentUser();

                if (currentUser == null)
                    throw new UnauthorizedException("Invalid user");

                if (org.OwnerId != currentUser.Id && org.OwnerId != null && !org.IsPublic)
                    throw new UnauthorizedException("This organization does not belong to the current user");
            }

            return org;
        }

        public async Task<Dataset> GetDatasetAndCheck(string orgSlug, string dsSlug, bool safe = false)
        {
            if (string.IsNullOrWhiteSpace(dsSlug))
                throw new BadRequestException("Missing dataset id");

            if (!IsSlugValid(dsSlug))
                throw new BadRequestException("Invalid dataset id");

            var org = await GetOrganizationAndCheck(orgSlug);

            var dataset = org.Datasets.FirstOrDefault(item => item.Slug == dsSlug);

            if (dataset == null)
            {
                if (safe) return null;
                throw new NotFoundException("Cannot find dataset");
            }

            return dataset;
        }
    }
}
