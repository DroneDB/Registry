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
        public Task<IEnumerable<OrganizationDto>> GetAll();
        public Task<OrganizationDto> Get(string id);
        public Task<OrganizationDto> AddNew(OrganizationDto dataset);
        public Task Edit(string id, OrganizationDto dataset);
        public Task Delete(string id);
    }
}
