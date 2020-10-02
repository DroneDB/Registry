using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Options;
using Registry.Web.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports
{
    public interface IOrganizationsManager 
    {
        public Task<IEnumerable<OrganizationDto>> List();
        public Task<OrganizationDto> Get(string orgSlug);
        public Task<OrganizationDto> AddNew(OrganizationDto organization);
        public Task Edit(string orgSlug, OrganizationDto organization);
        public Task Delete(string orgSlug);
    }
}
