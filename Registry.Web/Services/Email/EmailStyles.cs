#nullable enable
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Registry.Web.Services.Email;

/// <summary>
/// The fonts and colours of the e-mails, written inline (mail clients drop style sheets). A colour ends up in a
/// <c>style</c> attribute, so only a value this class validated as hex is ever written there: no configured text can
/// add CSS.
/// </summary>
public static partial class EmailStyles
{
    /// <summary>The system font of each platform; set on every cell, because Outlook does not inherit it into tables.</summary>
    public const string FontStack = "-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif";

    /// <summary>
    /// The accent when the instance sets no theme colour: the Hub's brand blue (<c>PRIMARY[500]</c> of the Hub's
    /// <c>libs/palette.js</c>; a unit test keeps the two equal).
    /// </summary>
    public const string BrandBlue = "#0070e0";

    /// <summary>Text on a light accent.</summary>
    public const string DarkText = "#111827";

    /// <summary>Text on a dark accent.</summary>
    public const string LightText = "#ffffff";

    /// <summary>A build that failed: fixed, not the accent, so it means the same on every instance.</summary>
    public const string Failed = "#b91c1c";

    /// <summary>A build that completed: fixed, not the accent, so it means the same on every instance.</summary>
    public const string Completed = "#15803d";

    [GeneratedRegex("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexPattern();

    /// <summary>
    /// <paramref name="value"/> as <c>#rrggbb</c> (lowercase; <c>#rgb</c> is expanded), or null when it is not a hex
    /// colour (a name, <c>rgb()</c>, or anything else).
    /// </summary>
    public static string? NormalizeHex(string? value)
    {
        var text = value?.Trim();
        if (text == null || !HexPattern().IsMatch(text))
            return null;

        text = text.ToLowerInvariant();
        return text.Length == 4 ? $"#{text[1]}{text[1]}{text[2]}{text[2]}{text[3]}{text[3]}" : text;
    }

    /// <summary>
    /// The text colour that reads best on <paramref name="background"/> (a <c>#rrggbb</c> colour): white or near-black,
    /// whichever has the higher contrast ratio (WCAG relative luminance).
    /// </summary>
    public static string TextOn(string background)
    {
        var hex = NormalizeHex(background) ?? throw new ArgumentException($"'{background}' is not a hex colour", nameof(background));

        var luminance = Luminance(hex);
        var withLight = Contrast(Luminance(LightText), luminance);
        var withDark = Contrast(luminance, Luminance(DarkText));

        return withLight >= withDark ? LightText : DarkText;
    }

    /// <summary>The contrast that text needs on its background to be readable (WCAG AA, normal text).</summary>
    public const double ReadableContrast = 4.5;

    /// <summary>
    /// <paramref name="color"/> (a <c>#rrggbb</c> colour) as text on the white body of an e-mail: the colour itself when it
    /// is readable there, else the same hue darkened just enough to be (a light theme colour such as yellow is a fine
    /// button or bar, but unreadable as link text).
    /// </summary>
    public static string ReadableOnWhite(string color)
    {
        var hex = NormalizeHex(color) ?? throw new ArgumentException($"'{color}' is not a hex colour", nameof(color));
        var white = Luminance(LightText);

        for (var shade = 1.0; shade > 0; shade -= 0.05)
        {
            var candidate = Scaled(hex, shade);
            if (Contrast(white, Luminance(candidate)) >= ReadableContrast)
                return candidate;
        }

        return DarkText;
    }

    // hex with each channel multiplied by factor (towards black)
    private static string Scaled(string hex, double factor)
    {
        static int Channel(string hex, int index, double factor) =>
            (int)Math.Round(int.Parse(hex.AsSpan(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) * factor);

        return string.Create(CultureInfo.InvariantCulture,
            $"#{Channel(hex, 1, factor):x2}{Channel(hex, 3, factor):x2}{Channel(hex, 5, factor):x2}");
    }

    private static double Contrast(double lighter, double darker) => (lighter + 0.05) / (darker + 0.05);

    private static double Luminance(string hex)
    {
        static double Channel(string hex, int index)
        {
            var value = int.Parse(hex.AsSpan(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(hex, 1) + 0.7152 * Channel(hex, 3) + 0.0722 * Channel(hex, 5);
    }
}
