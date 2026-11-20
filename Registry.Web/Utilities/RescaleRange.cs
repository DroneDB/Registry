using System;
using System.Globalization;
using Registry.Web.Exceptions;

namespace Registry.Web.Utilities;

/// <summary>
/// The <c>rescale</c> parameter of the raster render and analysis endpoints (FR-RAS-10): <c>min,max</c>, two finite
/// numbers with '.' as the decimal sign (the machine format; the Hub reads what the user types and sends this) and
/// <c>min</c> less than <c>max</c>. libddb reads the same format, and refuses a bad value with a failure the user would
/// see as a 500, so the Registry refuses it first, with a 400 at the <c>rescale</c> member (D-047).
/// </summary>
public static class RescaleRange
{
    /// <summary>The name of the request parameter.</summary>
    public const string Field = "rescale";

    /// <summary>The rule as a client shows it.</summary>
    public const string Description = "two numbers, min and max, separated by a comma, with min less than max (for example 0,255)";

    /// <summary>The 400 message of a value that is not a range.</summary>
    public static string InvalidMessage(string value) => $"The rescale value '{value}' is invalid: use {Description}";

    /// <summary>Whether <paramref name="value"/> is a range, and its two numbers.</summary>
    /// <param name="value">The parameter, as sent.</param>
    /// <param name="min">The first number.</param>
    /// <param name="max">The second number.</param>
    public static bool TryParse(string? value, out double min, out double max)
    {
        min = max = 0;
        if (value == null)
            return false;

        var parts = value.Split(',');
        return parts.Length == 2 && TryNumber(parts[0], out min) && TryNumber(parts[1], out max) && min < max;
    }

    /// <summary>
    /// The range to render: null when <paramref name="value"/> is null, empty or only spaces (the default range: libddb
    /// would refuse the spaces), else <paramref name="value"/> itself when it is a valid range.
    /// </summary>
    /// <exception cref="InvalidFieldException">The value is not a range.</exception>
    public static string? Validated(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return TryParse(value, out _, out _) ? value : throw new InvalidFieldException(Field, InvalidMessage(value));
    }

    // A finite number that libddb can read: it parses each one as a float (std::stof, vis_render.cpp
    // parseRescaleRange), which refuses a value beyond float.MaxValue ("1e39")
    private static bool TryNumber(string text, out double number) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
        double.IsFinite(number) && Math.Abs(number) <= float.MaxValue;
}
