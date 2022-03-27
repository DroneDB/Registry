using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Caching;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Entry = Registry.Ports.DroneDB.Models.Entry;

namespace Registry.Web.Utilities
{
    public static class Extenders
    {

        public static Organization ToEntity(this OrganizationDto organization)
        {
            return new()
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
            return new()
            {
                Slug = organization.Slug,
                Name = organization.Name,
                Description = organization.Description,
                CreationDate = organization.CreationDate,
                Owner = organization.OwnerId,
                IsPublic = organization.IsPublic
            };
        }

        /*public static Dataset ToEntity(this DatasetDto dataset)
        {
            var entity = new Dataset
            {
                Id = dataset.Id,
                Slug = dataset.Slug,
                CreationDate = dataset.CreationDate,
                Name = string.IsNullOrEmpty(dataset.Name) ? dataset.Slug : dataset.Name
            };
            return entity;
        }*/

        public static StampDto ToDto(this Stamp stamp)
        {
            if (stamp == null) return null;

            return new StampDto
            {
                Checksum = stamp.Checksum,
                Entries = stamp.Entries,
                Meta = stamp.Meta
            };
        }

        public static MetaDto ToDto(this Meta meta)
        {
            if (meta == null) return null;

            return new MetaDto
            {
                Data = meta.Data,
                Id = meta.Id,
                ModifiedTime = meta.ModifiedTime
            };
        }

        public static MetaListItemDto ToDto(this MetaListItem listItem)
        {
            if (listItem == null) return null;

            return new MetaListItemDto
            {
                Count = listItem.Count,
                Key = listItem.Key,
                Path = listItem.Path
            };
        }

        public static MetaDumpDto ToDto(this MetaDump md)
        {
            if (md == null) return null;

            return new MetaDumpDto
            {
                Id = md.Id,
                Path = md.Path,
                Key = md.Key,
                Data = md.Data,
                ModifiedTime = md.ModifiedTime
            };
        }

        public static EntryDto ToDto(this Entry entry)
        {
            if (entry == null) return null;
            
            return new EntryDto
            {
                Depth = entry.Depth,
                Path = entry.Path,
                Hash = entry.Hash,
                Properties = entry.Properties,
                Size = entry.Size,
                Type = entry.Type,
                ModifiedTime = entry.ModifiedTime,
                PointGeometry = entry.PointGeometry,
                PolygonGeometry = entry.PolygonGeometry
            };
        }
        
        public static DatasetDto ToDto(this Dataset dataset, Entry entry)
        {
            return new()
            {
                Slug = dataset.Slug,
                CreationDate = dataset.CreationDate,
                Properties = entry.Properties,
                Size = entry.Size
            };
        }

        // Only lowercase letters, numbers, dashes and underscore. Max length 128 and cannot start with a dash or underscore.
        private static readonly Regex SafeNameRegex = new(@"^[a-z0-9][a-z0-9_-]{1,128}$", RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>
        /// Checks if a string is a valid slug
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public static bool IsValidSlug(this string name)
        {
            return SafeNameRegex.IsMatch(name);
        }


        /// <summary>
        /// Converts a string to a slug
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public static string ToSlug(this string name)
        {

            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Cannot make slug from empty string");

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

            var str = new string(tmp.Select(c => 
                    char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '-').ToArray())
                .ToLowerInvariant();

            // If it starts with a period or a dash pad it with a 0
            var res = str[0] == '_' || str[0] == '-' ? "0" + str : str;

            return res.Length > 128 ? res[..128] : res;
        }

        /// <summary>
        /// Converts a string tag (organization/dataset) and checks if valid
        /// </summary>
        /// <param name="tag"></param>
        /// <returns></returns>
        public static TagDto ToTag(this string tag)
        {

            if (string.IsNullOrWhiteSpace(tag))
                return new TagDto(null, null);

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

        public static T ToObject<T>(this JsonElement obj)
        {
            return JsonConvert.DeserializeObject<T>(obj.GetRawText());
        }

        public static T ToObject<T>(this Dictionary<string, object> obj)
        {
            // Just don't ask please
            return JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(obj));
        }

        public static string ToErrorString(this IEnumerable<IdentityError> results)
        {
            if (results == null || !results.Any()) return "No error details";
            return string.Join(", ", results.Select(item => $"[{item.Code}: '{item.Description}']"));
        }

        public static string ToPrintableList(this IEnumerable<string> arr)
        {
            return arr == null ? "[]" : $"[{string.Join(", ", arr)}]";
        }

        public static async Task ErrorResult(this HttpResponse response, string message)
        {
            await Result(response, new ErrorResponse(message), StatusCodes.Status500InternalServerError);
        }
        
        public static async Task ErrorResult(this HttpResponse response, Exception ex)
        {
            await ErrorResult(response, ex.Message);
        }
        
        public static async Task Result<T>(this HttpResponse response, T result, int statusCode)
        {
            response.StatusCode = statusCode;
            await response.WriteAsJsonAsync(result);
        }

        public static byte[] ComputeHash(this HashAlgorithm hashAlgorithm, string inputFile)
        {
            using var stream = File.OpenRead(inputFile);
            return hashAlgorithm.ComputeHash(stream);
        }

        public static async Task<byte[]> ComputeHashAsync(this HashAlgorithm hashAlgorithm, string inputFile)
        {
            await using var stream = File.OpenRead(inputFile);
            return await hashAlgorithm.ComputeHashAsync(stream);
        }
        
        public static SafeMetaManager GetSafe(this IMetaManager manager)
        {
            return new SafeMetaManager(manager);
        }

    }

}
