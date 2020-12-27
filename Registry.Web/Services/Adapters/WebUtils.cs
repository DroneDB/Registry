using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNet.OData.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Registry.Common;
using Registry.Web.Controllers;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters
{

    // NOTE: This class is a fundamental piece of the architecture because 
    // it encapsulates all the validation logic of the organizations and datasets
    // The logic is centralized here because it could be subject to change

    public class WebUtils : IUtils
    {
        private readonly IAuthManager _authManager;
        private readonly RegistryContext _context;
        private readonly AppSettings _settings;
        private readonly IHttpContextAccessor _accessor;

        public WebUtils(IAuthManager authManager,
            RegistryContext context,
            IOptions<AppSettings> settings,
            IHttpContextAccessor accessor)
        {
            _authManager = authManager;
            _context = context;
            _accessor = accessor;
            _settings = settings.Value;
        }


        public async Task<Organization> GetOrganization(string orgSlug, bool safe = false, bool checkOwnership = true)
        {
            if (string.IsNullOrWhiteSpace(orgSlug))
                throw new BadRequestException("Missing organization id");

            if (!orgSlug.IsValidSlug())
                throw new BadRequestException("Invalid organization id");

            var org = _context.Organizations.Include(item => item.Datasets)
                .FirstOrDefault(item => item.Slug == orgSlug);

            if (org == null)
                return safe ? (Organization)null :
                    throw new NotFoundException("Organization not found");

            if (checkOwnership && !await _authManager.IsUserAdmin())
            {
                var currentUser = await _authManager.GetCurrentUser();

                if (currentUser == null)
                    throw new UnauthorizedException("Invalid user");

                if (org.OwnerId != currentUser.Id && org.OwnerId != null && !org.IsPublic)
                    throw new UnauthorizedException("This organization does not belong to the current user");

            }

            return org;

        }

        public async Task<Dataset> GetDataset(string orgSlug, string dsSlug, bool retNullIfNotFound = false, bool checkOwnership = true)
        {
            if (string.IsNullOrWhiteSpace(dsSlug))
                throw new BadRequestException("Missing dataset id");

            if (!dsSlug.IsValidSlug())
                throw new BadRequestException("Invalid dataset id");

            if (string.IsNullOrWhiteSpace(orgSlug))
                throw new BadRequestException("Missing organization id");

            if (!orgSlug.IsValidSlug())
                throw new BadRequestException("Invalid organization id");

            var org = _context.Organizations
                .Include(item => item.Datasets)
                .FirstOrDefault(item => item.Slug == orgSlug);

            if (org == null)
                throw new NotFoundException("Organization not found");
            
            var dataset = org.Datasets.FirstOrDefault(item => item.Slug == dsSlug);

            if (dataset == null)
            {
                if (retNullIfNotFound) return null;

                throw new NotFoundException("Cannot find dataset");
            }

            if (!dataset.IsPublic && checkOwnership && !await _authManager.IsUserAdmin())
            {
                var currentUser = await _authManager.GetCurrentUser();

                if (currentUser == null)
                    throw new UnauthorizedException("Invalid user");

                if (org.OwnerId != currentUser.Id && org.OwnerId != null && !org.IsPublic)
                    throw new UnauthorizedException("This organization does not belong to the current user");

            }

            return dataset;
        }

        public string GetFreeOrganizationSlug(string orgName)
        {
            if (string.IsNullOrWhiteSpace(orgName))
                throw new BadRequestException("Empty organization name");

            var slug = orgName.ToSlug();

            var res = slug;

            for (var n = 1; ; n++)
            {
                var org = _context.Organizations.FirstOrDefault(item => item.Slug == res);

                if (org == null) return res;

                res = slug + "-" + n;

            }

        }
        public EntryDto GetDatasetEntry(Dataset dataset)
        {
            return new EntryDto
            {
                ModifiedTime = dataset.LastEdit,
                Depth = 0,
                Size = dataset.Size,
                Path = GenerateDatasetUrl(dataset),
                Type = EntryType.DroneDb,
                Meta = dataset.Meta
            };
        }

        public string GenerateDatasetUrl(Dataset dataset)
        {

            var isHttps = false;
            string host;

            if (!string.IsNullOrWhiteSpace(_settings.ExternalUrlOverride))
            {

                var uri = new Uri(_settings.ExternalUrlOverride);

                isHttps = uri.Scheme.ToLowerInvariant() == "https";
                host = uri.Host;

                // Mmmm
                if (uri.Port != 443 && uri.Port != 80)
                    host += ":" + uri.Port;

            }
            else
            {
                var context = _accessor.HttpContext;
                host = context?.Request.Host.ToString() ?? "localhost";
                isHttps = context?.Request.IsHttps ?? false;
            }

            var scheme = isHttps ? "ddb" : "ddb+unsafe";

            var datasetUrl = string.Format($"{scheme}://{host}/{dataset.Organization.Slug}/{dataset.Slug}");

            return datasetUrl;

        }
    }
}
