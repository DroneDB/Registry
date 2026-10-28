using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Stac;

/// <summary>The query strings and paging tokens of the STAC API's links.</summary>
public static class StacQueries
{
    /// <summary>The 2D footprint libddb filters on, or null.</summary>
    public static string? BboxToString(double[]? bbox)
    {
        // Accept 2D ([minX,minY,maxX,maxY]) or 3D ([minX,minY,minZ,maxX,maxY,maxZ]) bboxes,
        // projecting to the 2D footprint the C++ core understands.
        double[]? flat = bbox switch
        {
            { Length: 4 } => bbox,
            { Length: 6 } => [bbox[0], bbox[1], bbox[3], bbox[4]],
            _ => null
        };

        return flat is null
            ? null
            : string.Join(",", flat.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
    }

    /// <summary>The query of a page link (next, prev): every paging parameter.</summary>
    public static string BuildItemsQuery(double[]? bbox, string? datetime, int limit, int offset)
    {
        var parts = new List<string> { $"limit={limit}", $"offset={offset}" };

        if (bbox is { Length: 4 } or { Length: 6 })
            parts.Add("bbox=" + Uri.EscapeDataString(
                string.Join(",", bbox.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))));

        if (!string.IsNullOrEmpty(datetime))
            parts.Add("datetime=" + Uri.EscapeDataString(datetime));

        return "?" + string.Join("&", parts);
    }

    /// <summary>The query of a self link: only the parameters the client gave (STAC API conformance).</summary>
    public static string BuildItemsSelfQuery(double[]? bbox, string? datetime, int? limit, int? offset)
    {
        var parts = new List<string>();

        if (bbox is { Length: 4 } or { Length: 6 })
            parts.Add("bbox=" + Uri.EscapeDataString(
                string.Join(",", bbox.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))));

        if (!string.IsNullOrEmpty(datetime))
            parts.Add("datetime=" + Uri.EscapeDataString(datetime));

        if (limit.HasValue)
            parts.Add($"limit={limit.Value}");

        if (offset.HasValue)
            parts.Add($"offset={offset.Value}");

        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }

    /// <summary>The query of the next page of a search.</summary>
    public static string BuildSearchQuery(StacSearchRequestDto request, string token, int limit)
    {
        var parts = new List<string> { $"limit={limit}", "token=" + Uri.EscapeDataString(token) };

        if (request.Bbox is { Length: 4 } or { Length: 6 })
            parts.Add("bbox=" + Uri.EscapeDataString(
                string.Join(",", request.Bbox.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))));

        if (!string.IsNullOrEmpty(request.Datetime))
            parts.Add("datetime=" + Uri.EscapeDataString(request.Datetime));

        if (request.Collections is { Length: > 0 })
            parts.Add("collections=" + Uri.EscapeDataString(string.Join(",", request.Collections)));

        return "?" + string.Join("&", parts);
    }

    /// <summary>The search paging token: the dataset to resume from and the offset in it.</summary>
    public static string EncodeToken(int datasetIndex, int offset)
    {
        var raw = $"{datasetIndex}:{offset}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The dataset and offset a search token names (0, 0 for none or an invalid one).</summary>
    public static void DecodeToken(string? token, out int datasetIndex, out int offset)
    {
        datasetIndex = 0;
        offset = 0;

        if (string.IsNullOrEmpty(token))
            return;

        try
        {
            var s = token.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }

            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(s));
            var split = raw.Split(':');
            if (split.Length == 2 &&
                int.TryParse(split[0], out var di) &&
                int.TryParse(split[1], out var off))
            {
                datasetIndex = Math.Max(di, 0);
                offset = Math.Max(off, 0);
            }
        }
        catch (FormatException)
        {
            // Invalid token: start from the beginning
        }
    }
}
