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

    /// <summary>The organization a request creates, owned by <paramref name="ownerId"/> and created at <paramref name="creationDate"/>.</summary>
    public static Organization ToEntity(this OrganizationRequestDto organization, string ownerId, DateTime creationDate)
    {
        return new Organization
        {
            Slug = organization.Slug,
            Name = string.IsNullOrEmpty(organization.Name) ? organization.Slug : organization.Name,
            Description = organization.Description,
            CreationDate = creationDate,
            OwnerId = ownerId,
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

    /// <summary>
    /// Converts a <see cref="Dataset"/> and its root <see cref="Entry"/> into a <see cref="DatasetDto"/>.
    /// </summary>
    /// <param name="dataset">The dataset entity to convert.</param>
    /// <param name="entry">The dataset's root entry, providing size and metadata.</param>
    /// <returns>The mapped <see cref="DatasetDto"/>.</returns>
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
    /// Validator for slugs: <see cref="SlugRules"/> (length, characters, no consecutive dashes), the one definition
    /// the routes and the slug generator share.
    /// </summary>
    public static bool IsValidSlug(this string s) => SlugRules.IsValid(s);

    /// <summary>
    /// High-performance slug generator.
    /// - Uses Unicode normalization (NFD) to strip diacritics.
    /// - Emits only ASCII [a-z0-9_-].
    /// - Replaces any non-allowed char with '-' and collapses consecutive dashes.
    /// - Ensures the length of <see cref="SlugRules"/> (cut to its maximum, padded with '0' to its minimum) and that
    ///   slug does not start with '-' or '_' (prefixes '0' if necessary, matching the original behavior): the
    ///   result is always a valid slug.
    /// </summary>
    public static string ToSlug(this string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Cannot make slug from empty string", nameof(name));

        // 1) Normalize to NFD so diacritics become NonSpacingMark and can be dropped
        var normalized = name.Normalize(NormalizationForm.FormD);

        // 2) Rent a buffer. Upper bound is min(MaxLength, normalized.Length) since we never write more than input length.
        var capacity = Math.Min(SlugRules.MaxLength, normalized.Length);
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

            // Enforce the length bounds (after potential prefix)
            if (slug.Length > SlugRules.MaxLength)
                slug = slug[..SlugRules.MaxLength];
            else if (slug.Length < SlugRules.MinLength)
                slug = slug.PadRight(SlugRules.MinLength, '0');

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
        return obj.Deserialize<T>(ApiJson.Options);
    }

    public static T ToObject<T>(this Dictionary<string, object> obj)
    {
        // The dictionary the configuration binder made (strings, numbers): as the type it describes
        return JsonSerializer.SerializeToElement(obj, ApiJson.Options).Deserialize<T>(ApiJson.Options);
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
    /// Decodes an image byte array and encodes it again in <paramref name="format"/>.
    /// </summary>
    /// <param name="bytes">The source image byte array.</param>
    /// <param name="format">The target format (the encoder of SkiaSharp: PNG, JPEG or WebP).</param>
    /// <param name="quality">The quality of the image (0-100); ignored by lossless PNG.</param>
    /// <returns>A byte array containing the encoded image.</returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public static byte[] Encode(this byte[] bytes, SKEncodedImageFormat format, int quality = 70)
    {
        if (bytes == null || bytes.Length == 0)
            throw new ArgumentException("Source image is null or empty.", nameof(bytes));

        if (quality is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(quality), "Quality must be between 0 and 100.");

        using var bitmap = SKBitmap.Decode(bytes) ?? throw new InvalidOperationException("Source image could not be decoded.");
        using var image = SKImage.FromBitmap(bitmap) ?? throw new InvalidOperationException("Failed to create image from bitmap.");
        using var data = image.Encode(format, quality) ?? throw new InvalidOperationException($"Failed to encode image to {format}.");

        return data.ToArray();
    }

}