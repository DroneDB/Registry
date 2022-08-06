﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace Registry.Common
{
    // Ref: https://github.com/restsharp/RestSharp/blob/dev/src/RestSharp/Extensions/StringExtensions.cs
    public static class StringExtensions
    {
        private static readonly Regex DateRegex = new Regex(@"\\?/Date\((-?\d+)(-|\+)?([0-9]{4})?\)\\?/");
        private static readonly Regex NewDateRegex = new Regex(@"newDate\((-?\d+)\)");

        private static readonly Regex IsUpperCaseRegex = new Regex(@"^[A-Z]+$");

        private static readonly Regex AddUnderscoresRegex1 = new Regex(@"[-\s]");
        private static readonly Regex AddUnderscoresRegex2 = new Regex(@"([a-z\d])([A-Z])");
        private static readonly Regex AddUnderscoresRegex3 = new Regex(@"([A-Z]+)([A-Z][a-z])");

        private static readonly Regex AddDashesRegex1 = new Regex(@"[\s]");
        private static readonly Regex AddDashesRegex2 = new Regex(@"([a-z\d])([A-Z])");
        private static readonly Regex AddDashesRegex3 = new Regex(@"([A-Z]+)([A-Z][a-z])");

        private static readonly Regex AddSpacesRegex1 = new Regex(@"[-\s]");
        private static readonly Regex AddSpacesRegex2 = new Regex(@"([a-z\d])([A-Z])");
        private static readonly Regex AddSpacesRegex3 = new Regex(@"([A-Z]+)([A-Z][a-z])");
        public static string UrlDecode(this string input) => HttpUtility.UrlDecode(input);

        /// <summary>
        /// Uses Uri.EscapeDataString() based on recommendations on MSDN
        /// http://blogs.msdn.com/b/yangxind/archive/2006/11/09/don-t-use-net-system-uri-unescapedatastring-in-url-decoding.aspx
        /// </summary>
        public static string UrlEncode(this string input)
        {
            const int maxLength = 32766;

            if (input == null)
                throw new ArgumentNullException(nameof(input));

            if (input.Length <= maxLength)
                return Uri.EscapeDataString(input);

            var sb = new StringBuilder(input.Length * 2);
            var index = 0;

            while (index < input.Length)
            {
                var length = Math.Min(input.Length - index, maxLength);

                while (CharUnicodeInfo.GetUnicodeCategory(input[index + length - 1]) == UnicodeCategory.Surrogate)
                {
                    length--;
                }

                var subString = input.Substring(index, length);

                sb.Append(Uri.EscapeDataString(subString));
                index += subString.Length;
            }

            return sb.ToString();
        }

        public static string UrlEncode(this string input, Encoding encoding)
        {
            var encoded = HttpUtility.UrlEncode(input, encoding);
            return encoded?.Replace("+", "%20");
        }

        /// <summary>
        /// Check that a string is not null or empty
        /// </summary>
        /// <param name="input">String to check</param>
        /// <returns>bool</returns>
        public static bool HasValue(this string input) => !string.IsNullOrEmpty(input);

        /// <summary>
        /// Remove underscores from a string
        /// </summary>
        /// <param name="input">String to process</param>
        /// <returns>string</returns>
        public static string RemoveUnderscoresAndDashes(this string input) => input.Replace("_", "").Replace("-", "");

        /// <summary>
        /// Parses most common JSON date formats
        /// </summary>
        /// <param name="input">JSON value to parse</param>
        /// <param name="culture"></param>
        /// <returns>DateTime</returns>
        public static DateTime ParseJsonDate(this string input, CultureInfo culture)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            
            const long maxAllowedTimestamp = 253402300799;

            input = input.Replace("\n", "");
            input = input.Replace("\r", "");
            input = input.RemoveSurroundingQuotes();

            if (long.TryParse(input, out var unix))
            {
                var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

                return unix > maxAllowedTimestamp ? epoch.AddMilliseconds(unix) : epoch.AddSeconds(unix);
            }

            if (input.Contains("/Date("))
                return ExtractDate(input, DateRegex, culture);

            if (input.Contains("new Date("))
            {
                input = input.Replace(" ", "");

                // because all whitespace is removed, match against newDate( instead of new Date(
                return ExtractDate(input, NewDateRegex, culture);
            }

            return ParseFormattedDate(input, culture);
        }

        private static string RemoveSurroundingQuotes(this string input)
        {
            if (input.StartsWith("\"") && input.EndsWith("\""))
                input = input.Substring(1, input.Length - 2);

            return input;
        }

        private static DateTime ParseFormattedDate(string input, CultureInfo culture)
        {
            string[] formats =
            {
                "u",
                "s",
                "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'",
                "yyyy-MM-ddTHH:mm:ssZ",
                "yyyy-MM-dd HH:mm:ssZ",
                "yyyy-MM-ddTHH:mm:ss",
                "yyyy-MM-ddTHH:mm:sszzzzzz",
                "yyyy-MM-ddTHH:mm:ss.fffZ",
                "M/d/yyyy h:mm:ss tt" // default format for invariant culture
            };

            if (DateTime.TryParseExact(
                input, formats, culture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date
            ))
                return date;

            return DateTime.TryParse(input, culture, DateTimeStyles.None, out date) ? date : default;
        }

        private static DateTime ExtractDate(string input, Regex regex, CultureInfo culture)
        {
            var dt = DateTime.MinValue;

            if (!regex.IsMatch(input)) return dt;

            var matches = regex.Matches(input);
            var match = matches[0];
            var ms = Convert.ToInt64(match.Groups[1].Value);
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            dt = epoch.AddMilliseconds(ms);

            // adjust if time zone modifier present
            if (match.Groups.Count <= 2 || string.IsNullOrEmpty(match.Groups[3].Value)) return dt;

            var mod = DateTime.ParseExact(match.Groups[3].Value, "HHmm", culture);

            dt = match.Groups[2].Value == "+"
                ? dt.Add(mod.TimeOfDay)
                : dt.Subtract(mod.TimeOfDay);

            return dt;
        }

        /// <summary>
        /// Converts a string to pascal case
        /// </summary>
        /// <param name="lowercaseAndUnderscoredWord">String to convert</param>
        /// <param name="culture"></param>
        /// <returns>string</returns>
        public static string ToPascalCase(this string lowercaseAndUnderscoredWord, CultureInfo culture)
            => ToPascalCase(lowercaseAndUnderscoredWord, true, culture);

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
        /// Converts a string to camel case
        /// </summary>
        /// <param name="lowercaseAndUnderscoredWord">String to convert</param>
        /// <param name="culture"></param>
        /// <returns>String</returns>
        public static string ToCamelCase(this string lowercaseAndUnderscoredWord, CultureInfo culture)
            => MakeInitialLowerCase(ToPascalCase(lowercaseAndUnderscoredWord, culture), culture);

        /// <summary>
        /// Convert the first letter of a string to lower case
        /// </summary>
        /// <param name="word">String to convert</param>
        /// <param name="culture"></param>
        /// <returns>string</returns>
        public static string MakeInitialLowerCase(this string word, CultureInfo culture) =>
            string.Concat(word[..1].ToLower(culture), word[1..]);

        /// <summary>
        /// Add underscores to a pascal-cased string
        /// </summary>
        /// <param name="pascalCasedWord">String to convert</param>
        /// <returns>string</returns>
        public static string AddUnderscores(this string pascalCasedWord)
            => AddUnderscoresRegex1.Replace(
                AddUnderscoresRegex2.Replace(
                    AddUnderscoresRegex3.Replace(pascalCasedWord, "$1_$2"),
                    "$1_$2"
                ),
                "_"
            );

        /// <summary>
        /// Add dashes to a pascal-cased string
        /// </summary>
        /// <param name="pascalCasedWord">String to convert</param>
        /// <returns>string</returns>
        public static string AddDashes(this string pascalCasedWord)
            => AddDashesRegex1.Replace(
                AddDashesRegex2.Replace(
                    AddDashesRegex3.Replace(pascalCasedWord, "$1-$2"),
                    "$1-$2"
                ),
                "-"
            );

        /// <summary>
        ///     Checks to see if a string is all uppper case
        /// </summary>
        /// <param name="inputString">String to check</param>
        /// <returns>bool</returns>
        public static bool IsUpperCase(this string inputString) => IsUpperCaseRegex.IsMatch(inputString);

        /// <summary>
        /// Add an underscore prefix to a pascal-cased string
        /// </summary>
        /// <param name="pascalCasedWord"></param>
        /// <returns></returns>
        public static string AddUnderscorePrefix(this string pascalCasedWord) => $"_{pascalCasedWord}";

        /// <summary>
        /// Add spaces to a pascal-cased string
        /// </summary>
        /// <param name="pascalCasedWord">String to convert</param>
        /// <returns>string</returns>
        public static string AddSpaces(this string pascalCasedWord)
            => AddSpacesRegex1.Replace(
                AddSpacesRegex2.Replace(
                    AddSpacesRegex3.Replace(pascalCasedWord, "$1 $2"),
                    "$1 $2"
                ),
                " "
            );

        internal static bool IsEmpty(this string value) => string.IsNullOrWhiteSpace(value);

        internal static bool IsNotEmpty(this string value) => !string.IsNullOrWhiteSpace(value);

        /// <summary>
        /// Return possible variants of a name for name matching.
        /// </summary>
        /// <param name="name">String to convert</param>
        /// <param name="culture">The culture to use for conversion</param>
        /// <returns>IEnumerable&lt;string&gt;</returns>
        public static IEnumerable<string> GetNameVariants(this string name, CultureInfo culture)
        {
            if (string.IsNullOrEmpty(name))
                yield break;

            yield return name;

            // try camel cased name
            yield return name.ToCamelCase(culture);

            // try lower cased name
            yield return name.ToLower(culture);

            // try name with underscores
            yield return name.AddUnderscores();

            // try name with underscores with lower case
            yield return name.AddUnderscores().ToLower(culture);

            // try name with dashes
            yield return name.AddDashes();

            // try name with dashes with lower case
            yield return name.AddDashes().ToLower(culture);

            // try name with underscore prefix
            yield return name.AddUnderscorePrefix();

            // try name with proper camel case
            yield return name.AddUnderscores().ToCamelCase(culture);

            // try name with underscore prefix, using proper camel case
            yield return name.ToCamelCase(culture).AddUnderscorePrefix();

            // try name with underscore prefix, using camel case
            yield return name.AddUnderscores().ToCamelCase(culture).AddUnderscorePrefix();

            // try name with spaces
            yield return name.AddSpaces();

            // try name with spaces with lower case
            yield return name.AddSpaces().ToLower(culture);
        }

        internal static string JoinToString<T>(this IEnumerable<T> collection, string separator,
            Func<T, string> getString)
            => JoinToString(collection.Select(getString), separator);

        internal static string JoinToString(this IEnumerable<string> strings, string separator) =>
            string.Join(separator, strings);
    }
}