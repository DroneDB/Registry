using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web
{
    public static class Extenders
    {
        public static Organization ToEntity(this OrganizationDto organization)
        {
            return new Organization
            {
                Slug = organization.Slug,
                Name = string.IsNullOrEmpty(organization.Name) ? organization.Slug : organization.Name,
                Description = organization.Description,
                CreationDate = organization.CreationDate,
                OwnerId = organization.Owner,
                IsPublic = organization.IsPublic
            };
        }

        public static OrganizationDto ToDto(this Organization organization)
        {
            return new OrganizationDto
            {
                Slug = organization.Slug,
                Name = organization.Name,
                Description = organization.Description,
                CreationDate = organization.CreationDate,
                Owner = organization.OwnerId,
                IsPublic = organization.IsPublic
            };
        }

        public static Dataset ToEntity(this DatasetDto dataset)
        {
            return new Dataset
            {
                Id = dataset.Id,
                Slug = dataset.Slug,
                CreationDate = dataset.CreationDate,
                Description = dataset.Description,
                LastEdit = dataset.LastEdit,
                Name = string.IsNullOrEmpty(dataset.Name) ? dataset.Slug : dataset.Name,
                License = dataset.License,
                Meta = dataset.Meta,
                ObjectsCount = dataset.ObjectsCount,
                Size = dataset.Size,
                IsPublic = dataset.IsPublic
            };
        }

        public static DatasetDto ToDto(this Dataset dataset)
        {
            return new DatasetDto
            {
                Id = dataset.Id,
                Slug = dataset.Slug,
                CreationDate = dataset.CreationDate,
                Description = dataset.Description,
                LastEdit = dataset.LastEdit,
                Name = dataset.Name,
                License = dataset.License,
                Meta = dataset.Meta,
                ObjectsCount = dataset.ObjectsCount,
                Size = dataset.Size
            };
        }

        public static ObjectDto ToDto(this DdbEntry obj)
        {
            return new ObjectDto
            {
                Depth = obj.Depth,
                Hash = obj.Hash,
                Id = obj.Id,
                Meta = obj.Meta,
                ModifiedTime = obj.ModifiedTime,
                Path = obj.Path,
                PointGeometry = obj.PointGeometry,
                PolygonGeometry = obj.PolygonGeometry,
                Size = obj.Size,
                Type = obj.Type
            };
        }

        public static void UpdateStatistics(this Dataset ds, IDdb ddb)
        {
            var objs = ddb.Search(null).ToArray();

            ds.ObjectsCount = objs.Length;
            ds.Size = objs.Sum(item => item.Size);
            
        }

        // Only lowercase letters, numbers, - and _. Max length 255
        private static readonly Regex _safeNameRegex = new Regex(@"^[a-z\d\-_]{1,255}$", RegexOptions.Compiled | RegexOptions.Singleline);
        
        /// <summary>
        /// Checks if a string is a valid slug
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public static bool IsValidSlug(this string name)
        {
            return _safeNameRegex.IsMatch(name);
        }

        
        /// <summary>
        /// Converts a string to a slug
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public static string ToSlug(this string name)
        {

            Encoding enc;

            try
            {
                enc = Encoding.GetEncoding("ISO-8859-8");
            }
            catch (ArgumentException)
            {
                // Needed to use the ISO-8859-8 encoding
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                enc = Encoding.GetEncoding("ISO-8859-8");
            }

            var tempBytes = enc.GetBytes(name);
            var tmp = Encoding.UTF8.GetString(tempBytes);

            var res = new string(tmp.Select(c => char.IsSeparator(c) ? '-' : c).ToArray());

            return res.ToLowerInvariant();
        }

        /// <summary>
        /// Converts a string tag (organization/dataset) and checks if valid
        /// </summary>
        /// <param name="tag"></param>
        /// <returns></returns>
        public static TagDto ToTag(this string tag)
        {

            if (string.IsNullOrWhiteSpace(tag)) 
                throw new FormatException("Tag is null or empty");

            var sections = tag.Split('/');

            if (sections.Length != 2)
                throw new FormatException($"Unexpected tag format: '{tag}'");

            var org = sections[0];

            if (!org.IsValidSlug())
                throw new FormatException($"Organization slug is not valid: '{org}'");

            var ds = sections[1];

            if (!ds.IsValidSlug())
                throw new FormatException($"Dataset slug is not valid: '{ds}'");

            return new TagDto(org, ds);

        }

        
    }
}
