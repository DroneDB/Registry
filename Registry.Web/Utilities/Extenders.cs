using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using SkiaSharp;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Utilities;

public static class Extenders
{
    /// <summary>The organization a request creates, owned by <paramref name="ownerId"/> and created at <paramref name="creationDate"/>.</summary>
    public static Organization ToEntity(this OrganizationRequestDto organization, string? ownerId, DateTime creationDate)
    {
        return new Organization
        {
            Slug = organization.Slug!,
            Name = string.IsNullOrEmpty(organization.Name) ? organization.Slug! : organization.Name,
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

    [return: NotNullIfNotNull(nameof(stamp))]
    public static StampDto? ToDto(this Stamp? stamp)
    {
        if (stamp == null) return null;

        return new StampDto
        {
            Checksum = stamp.Checksum,
            Entries = stamp.Entries,
            Meta = stamp.Meta
        };
    }

    [return: NotNullIfNotNull(nameof(meta))]
    public static MetaDto? ToDto(this Meta? meta)
    {
        if (meta == null) return null;

        return new MetaDto
        {
            Data = meta.Data,
            Id = meta.Id,
            ModifiedTime = meta.ModifiedTime
        };
    }

    [return: NotNullIfNotNull(nameof(listItem))]
    public static MetaListItemDto? ToDto(this MetaListItem? listItem)
    {
        if (listItem == null) return null;

        return new MetaListItemDto
        {
            Count = listItem.Count,
            Key = listItem.Key,
            Path = listItem.Path
        };
    }

    [return: NotNullIfNotNull(nameof(md))]
    public static MetaDumpDto? ToDto(this MetaDump? md)
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

    [return: NotNullIfNotNull(nameof(entry))]
    public static EntryDto? ToDto(this Entry? entry)
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
    /// Converts a <see cref="Dataset"/> and its root <see cref="Entry"/> into a <see cref="DatasetDto"/>, without the
    /// saved map views of its metadata (<see cref="DatasetMeta.WithoutViews"/>).
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
            Properties = DatasetMeta.WithoutViews(entry.Properties),
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
    /// - Ignores the whitespace around the name, uses Unicode normalization (NFD) to strip diacritics, and transliterates the letters NFD does not decompose
    ///   (ø to o, æ to ae, ß to ss, ł to l, FR-ORG-05).
    /// - Emits only ASCII [a-z0-9_-].
    /// - Replaces any non-allowed char with '-' and collapses consecutive dashes.
    /// - Ensures the length of <see cref="SlugRules"/> (cut to its maximum, a trailing '-' or '_' that the cut or the end of
    ///   the name leaves removed, padded with '0' to its minimum) and that the slug does not start with '-' or '_'
    ///   (prefixes '0' if necessary, matching the original behavior): the result is always a valid slug that never
    ///   ends with '-' or '_' (FR-ORG-14).
    /// The Hub's <c>slugFromName</c> (libs/slug.js) gives the same result: the shared test cases (<c>toSlug</c>) check both.
    /// </summary>
    public static string ToSlug(this string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Cannot make slug from empty string", nameof(name));

        return WithFallback(SlugBody(name, legacy: false), legacy: false);
    }

    /// <summary>
    /// The slug <see cref="ToSlug"/> made before FR-ORG-05 and FR-ORG-14: no trim of the name, no transliteration, and a
    /// trailing '-' or '_' kept. Only to find what was stored with it (the default organization of a user whose name
    /// ends with '-', '_', '.', '@' or '+'); never to make a new slug.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The slug of the former rule.</returns>
    public static string ToLegacySlug(this string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Cannot make slug from empty string", nameof(name));

        return WithFallback(SlugBody(name, legacy: true), legacy: true);
    }

    /// <summary>
    /// Whether the characters <see cref="ToSlug"/> makes from <paramref name="name"/>, before its fallback (the '0'
    /// it adds at the start or pads with), hold a Latin letter or a digit (D-035).
    /// </summary>
    /// <returns>False for a null or blank name.</returns>
    public static bool SlugHasLatinLetterOrDigit(string? name) =>
        !string.IsNullOrWhiteSpace(name) && SlugBody(name, legacy: false).Any(c => c is >= 'a' and <= 'z' or >= '0' and <= '9');

    // What the generator writes for the name (at most SlugRules.MaxLength characters), before its fallback
    private static string SlugBody(string name, bool legacy)
    {
        // 1) Normalize to NFD so diacritics become NonSpacingMark and can be dropped (the spaces around the name are not
        //    part of it: they would only leave a dash at the ends; the former rule kept them)
        var normalized = (legacy ? name : name.Trim()).Normalize(NormalizationForm.FormD);

        // 2) Rent a buffer: never more than the maximum is written (a transliteration may write two characters)
        var buffer = ArrayPool<char>.Shared.Rent(SlugRules.MaxLength + 1);
        var w = 0;                // write index
        var prevDash = false;    // used to collapse consecutive dashes

        void Write(char outc)
        {
            // Collapse consecutive dashes
            if (outc == '-')
            {
                if (prevDash) return;
                prevDash = true;
            }
            else
            {
                prevDash = false;
            }

            if (w < SlugRules.MaxLength)
                buffer[w++] = outc;
        }

        try
        {
            for (var i = 0; i < normalized.Length && w < SlugRules.MaxLength; i++)
            {
                var c = normalized[i];

                // Drop combining marks (diacritics) produced by NFD
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                    continue;

                // The letters NFD leaves whole (FR-ORG-05; the former rule made them '-')
                var transliteration = legacy ? null : Transliterate(c);
                if (transliteration != null)
                {
                    foreach (var t in transliteration)
                        Write(t);
                    continue;
                }

                // Fast ASCII lowercasing: if 'A'..'Z', map to 'a'..'z'
                if (c >= 'A' && c <= 'Z') c = (char)(c | 0x20);

                // Only ASCII [a-z0-9_-] are kept; everything else becomes '-'
                var isLower = c is >= 'a' and <= 'z';
                var isDigit = c is >= '0' and <= '9';
                var isUnderscore = c == '_';
                var isDash = c == '-';

                Write((isLower || isDigit || isUnderscore || isDash) ? c : '-');
            }

            // Build the string from the written portion
            return new string(buffer, 0, w);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// The slugs the default organization of <paramref name="userName"/> was made with: <see cref="ToSlug"/>, then
    /// <see cref="ToLegacySlug"/> when it differs (an organization made before FR-ORG-14 keeps its slug).
    /// </summary>
    /// <param name="userName">The user name.</param>
    /// <returns>One or two slugs, the current rule first.</returns>
    public static string[] DefaultOrganizationSlugs(string userName)
    {
        var slug = userName.ToSlug();
        var legacy = userName.ToLegacySlug();
        return slug == legacy ? [slug] : [slug, legacy];
    }

    /// <summary>
    /// How well <paramref name="slug"/> matches the default organization of <paramref name="userName"/>: 0 for its
    /// slug, 1 for the slug of the former rule, 2 for either with the suffix "-&lt;n&gt;" a taken slug gets
    /// (<c>GetFreeOrganizationSlugAsync</c>), null for any other slug.
    /// </summary>
    /// <param name="slug">The slug of an organization.</param>
    /// <param name="userName">The user name.</param>
    /// <returns>The rank, lower is closer; null when it is not a default slug of the user.</returns>
    public static int? DefaultOrganizationSlugRank(string slug, string userName)
    {
        var slugs = DefaultOrganizationSlugs(userName);
        for (var i = 0; i < slugs.Length; i++)
            if (string.Equals(slug, slugs[i], StringComparison.Ordinal))
                return i;

        foreach (var candidate in slugs)
            if (slug.Length > candidate.Length + 1 && slug.StartsWith(candidate + "-", StringComparison.Ordinal) &&
                slug.AsSpan(candidate.Length + 1).IndexOfAnyExceptInRange('0', '9') < 0)
                return 2;

        return null;
    }

    // The fallback of the generator: a valid slug from what it wrote
    private static string WithFallback(string slug, bool legacy)
    {
        // If empty (e.g., all were dropped), return "0"
        if (slug.Length == 0) return "0";

        // If it starts with '-' or '_', prefix '0' (keeps original semantics)
        if (slug[0] == '-' || slug[0] == '_')
            slug = "0" + slug;

        // Enforce the length bounds (after potential prefix), then the end: a cut at the maximum or the end of the
        // name can leave a '-' or '_' there
        if (slug.Length > SlugRules.MaxLength)
            slug = slug[..SlugRules.MaxLength];

        if (!legacy)
            slug = slug.TrimEnd('-', '_');
        if (slug.Length < SlugRules.MinLength)
            slug = slug.PadRight(SlugRules.MinLength, '0');

        return slug;
    }

    // The letters that NFD does not decompose and a slug should still keep as ASCII (ø, æ, ß, ł and their capitals)
    private static string? Transliterate(char c) => c switch
    {
        'ø' or 'Ø' => "o",
        'æ' or 'Æ' => "ae",
        'ß' or 'ẞ' => "ss",
        'ł' or 'Ł' => "l",
        _ => null
    };


    /// <summary>
    /// Converts a string tag (organization/dataset) and checks if valid
    /// </summary>
    /// <param name="tag"></param>
    /// <returns>A TagDto containing the organization and dataset slugs, or null if the tag string is null, empty, or whitespace.</returns>
    public static TagDto? ToTag(this string? tag)
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

    public static T? ToObject<T>(this Dictionary<string, object>? obj)
    {
        // The dictionary the configuration binder made (strings, numbers): as the type it describes
        return JsonSerializer.SerializeToElement(obj, ApiJson.Options).Deserialize<T>(ApiJson.Options);
    }

    public static string ToErrorString(this IEnumerable<IdentityError>? results)
    {
        if (results == null || !results.Any()) return "No error details";
        return string.Join(", ", results.Select(item => $"[{item.Code}: '{item.Description}']"));
    }

    public static SafeMetaManager GetSafe(this IMetaManager manager)
    {
        return new SafeMetaManager(manager);
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