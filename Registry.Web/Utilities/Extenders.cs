using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Registry.Common.Model;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using SkiaSharp;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Utilities;

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

        /// <summary>
        /// Converts a tag string in the format "organization/dataset" to a TagDto object.
        /// </summary>
        /// <param name="tag">The tag string to convert, formatted as "organization/dataset".</param>
        /// <returns>
        /// A TagDto containing the organization and dataset slugs, or null if the tag string is null, empty, or whitespace.
        /// </returns>
        /// <exception cref="FormatException">
        /// Thrown when the tag string is not in the expected "organization/dataset" format,
        /// or when either the organization or dataset slug is invalid.
        /// </exception>
    /// Fast validator for slugs.
    /// Rules:
    /// - length 1..128
    /// - first char must be [a-z0-9]
    /// - allowed chars are [a-z0-9_-]
    /// - no consecutive dashes ("--") to match ToSlug collapsing behavior
    /// </summary>
    public static bool IsValidSlug(this string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (s.Length > 128) return false;

        // first must be [a-z0-9]
        var first = s[0];
        var firstIsLower = first >= 'a' && first <= 'z';
        var firstIsDigit = first >= '0' && first <= '9';
        if (!(firstIsLower || firstIsDigit)) return false;

        var prevDash = false;

        foreach (var c in s)
        {
            switch (c)
            {
                // [a-z]
                case >= 'a' and <= 'z':
                // [0-9]
                case >= '0' and <= '9':
                // underscore
                case '_':
                    prevDash = false; continue;
                // dash (reject if consecutive)
                case '-' when prevDash:
                    return false;
                case '-':
                    prevDash = true;
                    continue;
                default:
                    // anything else -> invalid
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// High-performance slug generator.
    /// - Uses Unicode normalization (NFD) to strip diacritics.
    /// - Emits only ASCII [a-z0-9_-].
    /// - Replaces any non-allowed char with '-' and collapses consecutive dashes.
    /// - Ensures max length 128 and that slug does not start with '-' or '_'
    ///   (prefixes '0' if necessary, matching the original behavior).
    /// </summary>
    public static string ToSlug(this string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Cannot make slug from empty string", nameof(name));

        // 1) Normalize to NFD so diacritics become NonSpacingMark and can be dropped
        var normalized = name.Normalize(NormalizationForm.FormD);

        // 2) Rent a buffer. Upper bound is min(128, normalized.Length) since we never write more than input length.
        var capacity = Math.Min(128, normalized.Length);
        if (capacity == 0) return "0";

        var buffer = ArrayPool<char>.Shared.Rent(capacity);
        var w = 0;                // write index
        var prevDash = false;    // used to collapse consecutive dashes

        try
        {
            for (var i = 0; i < normalized.Length && w < capacity; i++)
            {
                var c = normalized[i];

                // Drop combining marks (diacritics) produced by NFD
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                    continue;

                // Fast ASCII lowercasing: if 'A'..'Z', map to 'a'..'z'
                if (c >= 'A' && c <= 'Z') c = (char)(c | 0x20);

                // Only ASCII [a-z0-9_-] are kept; everything else becomes '-'
                var isLower = c is >= 'a' and <= 'z';
                var isDigit = c is >= '0' and <= '9';
                var isUnderscore = c == '_';
                var isDash = c == '-';

                var outc = (isLower || isDigit || isUnderscore || isDash) ? c : '-';

                // Collapse consecutive dashes
                if (outc == '-')
                {
                    if (prevDash) continue;
                    prevDash = true;
                }
                else
                {
                    prevDash = false;
                }

                buffer[w++] = outc;
            }

            // Build the string from the written portion
            var slug = new string(buffer, 0, w);

            // If empty (e.g., all were dropped), return "0"
            if (slug.Length == 0) return "0";

            // If it starts with '-' or '_', prefix '0' (keeps original semantics)
            if (slug[0] == '-' || slug[0] == '_')
                slug = "0" + slug;

            // Enforce max length 128 (after potential prefix)
            if (slug.Length > 128)
                slug = slug[..128];

            return slug;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }


    /// <summary>
    /// Converts a string tag (organization/dataset) and checks if valid
    /// </summary>
    /// <param name="tag"></param>
    /// <returns>A TagDto containing the organization and dataset slugs, or null if the tag string is null, empty, or whitespace.</returns>
    public static TagDto ToTag(this string tag)
    {

        if (string.IsNullOrWhiteSpace(tag))
            return null;

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

    public static bool IsPublicOrUnlisted(this SafeMetaManager meta)
    {
        return meta.Visibility is Visibility.Public or Visibility.Unlisted;
    }

    /// <summary>
    /// Converts an image byte array to WebP format with the specified quality.
    /// </summary>
    /// <param name="bytes">The source image byte array.</param>
    /// <param name="quality">The quality of the WebP image (0-100). Default is 70.</param>
    /// <returns>A byte array containing the WebP image.</returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public static byte[] ToWebp(this byte[] bytes, int quality = 70)
    {
        if (bytes == null || bytes.Length == 0)
            throw new ArgumentException("Source image is null or empty.", nameof(bytes));

        if (quality is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(quality), "Quality must be between 0 and 100.");

        using var bitmap = SKBitmap.Decode(bytes) ?? throw new InvalidOperationException("Source image could not be decoded.");
        using var image = SKImage.FromBitmap(bitmap) ?? throw new InvalidOperationException("Failed to create image from bitmap.");
        using var data = image.Encode(SKEncodedImageFormat.Webp, quality) ?? throw new InvalidOperationException("Failed to encode image to WebP format.");

        return data.ToArray();
    }

}