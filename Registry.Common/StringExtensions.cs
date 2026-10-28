#nullable disable
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Registry.Common;

/// <summary>
/// String extension methods for PascalCase conversion.
/// </summary>
// Ref: https://github.com/restsharp/RestSharp/blob/dev/src/RestSharp/Extensions/StringExtensions.cs
public static class StringExtensions
{
    private static readonly Regex IsUpperCaseRegex = new Regex(@"^[A-Z]+$");

    /// <summary>
    /// Converts a string to pascal case with the option to remove underscores
    /// </summary>
    /// <param name="text">String to convert</param>
    /// <param name="removeUnderscores">Option to remove underscores</param>
    /// <param name="culture"></param>
    /// <returns></returns>
    public static string ToPascalCase(this string text, bool removeUnderscores, CultureInfo culture)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        text = text.Replace('_', ' ');

        var joinString = removeUnderscores ? string.Empty : "_";
        var words = text.Split(' ');

        return words
            .Where(x => x.Length > 0)
            .Select(CaseWord)
            .JoinToString(joinString);

        string CaseWord(string word)
        {
            var restOfWord = word[1..];
            var firstChar = char.ToUpper(word[0], culture);

            if (restOfWord.IsUpperCase())
                restOfWord = restOfWord.ToLower(culture);

            return string.Concat(firstChar, restOfWord);
        }
    }

    /// <summary>
    ///     Checks to see if a string is all uppper case
    /// </summary>
    /// <param name="inputString">String to check</param>
    /// <returns>bool</returns>
    private static bool IsUpperCase(this string inputString) => IsUpperCaseRegex.IsMatch(inputString);

    private static string JoinToString(this IEnumerable<string> strings, string separator) =>
        string.Join(separator, strings);
}