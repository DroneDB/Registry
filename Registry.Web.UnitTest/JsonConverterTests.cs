#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using Newtonsoft.Json.Converters;
using JsonConvert = Newtonsoft.Json.JsonConvert;
using JsonSerializerSettings = Newtonsoft.Json.JsonSerializerSettings;
using DateTimeZoneHandling = Newtonsoft.Json.DateTimeZoneHandling;
using DateFormatHandling = Newtonsoft.Json.DateFormatHandling;
using DateParseHandling = Newtonsoft.Json.DateParseHandling;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Ports.Json;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Each converter of <see cref="RegistryJson"/> against the Json.NET output it replaces (the settings MVC used:
/// <c>DateTimeZoneHandling.Utc</c>, <c>IsoDateFormat</c>), value by value. The wire tests (shape, binding, goldens) prove
/// the DTOs; these prove the primitives behind them, including the values no DTO of the fixtures happens to carry.
/// </summary>
[TestFixture]
public sealed class JsonConverterTests
{
    private static readonly JsonSerializerOptions Options = RegistryJson.CreateOptions();

    private static readonly JsonSerializerSettings Legacy = new()
    {
        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        DateFormatHandling = DateFormatHandling.IsoDateFormat
    };

    private static string Now<T>(T value) => JsonSerializer.Serialize(value, Options);

    private static string Was<T>(T value) => JsonConvert.SerializeObject(value, Legacy);

    public static IEnumerable<double> Doubles() =>
    [
        0, 1, -1, 1.0, 2.5, 0.1, 0.30000000000000004, 1e-5, 1.5e20, 123456789.125, 100, 1e15, 1e16, -0.0,
        double.MaxValue, double.MinValue, double.Epsilon, double.NaN, double.PositiveInfinity, double.NegativeInfinity,
        45.123456789012345, -91.994083
    ];

    [TestCaseSource(nameof(Doubles))]
    public void Double_IsWrittenAsJsonNetWroteIt(double value) => Now(value).ShouldBe(Was(value));

    [TestCase(0f)]
    [TestCase(1f)]
    [TestCase(1.5f)]
    [TestCase(0.1f)]
    [TestCase(-90f)]
    [TestCase(1e-5f)]
    [TestCase(float.MaxValue)]
    public void Single_IsWrittenAsJsonNetWroteIt(float value) => Now(value).ShouldBe(Was(value));

    [TestCase("1.0", 1.0)]
    [TestCase("2", 2.0)]
    [TestCase("\"3.5\"", 3.5)]
    [TestCase("\"1e2\"", 100.0)]
    [TestCase("\"NaN\"", double.NaN)]
    public void Double_IsReadFromANumberOrAString(string json, double expected) =>
        JsonSerializer.Deserialize<double>(json, Options).ShouldBe(expected);

    public static IEnumerable<DateTime> Dates() =>
    [
        new(2026, 10, 21, 7, 31, 17, DateTimeKind.Utc),
        new(2026, 10, 21, 7, 31, 17, 500, DateTimeKind.Utc),
        new DateTime(2026, 10, 21, 7, 31, 17, DateTimeKind.Utc).AddTicks(1234567),
        new DateTime(2026, 10, 21, 7, 31, 17, DateTimeKind.Utc).AddTicks(10),
        new(2026, 10, 21, 7, 31, 17, DateTimeKind.Unspecified),
        new(2026, 10, 21, 7, 31, 17, 120, DateTimeKind.Unspecified),
        new(2026, 10, 21, 7, 31, 17, DateTimeKind.Local),
        new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        default,
        DateTime.MaxValue
    ];

    [TestCaseSource(nameof(Dates))]
    public void DateTime_IsWrittenAsAnIsoUtcInstant_LikeJsonNetWithUtcHandling(DateTime value) =>
        Now(value).ShouldBe(Was(value));

    [TestCase("\"2026-10-21T07:31:17Z\"")]
    [TestCase("\"2026-10-21T07:31:17.1234567Z\"")]
    [TestCase("\"2026-10-21T09:31:17+02:00\"")]
    [TestCase("\"2026-10-21T07:31:17\"")]
    [TestCase("\"2026-10-21\"")]
    public void DateTime_IsReadAsUtc_LikeJsonNetWithUtcHandling(string json)
    {
        var now = JsonSerializer.Deserialize<DateTime>(json, Options);
        var was = JsonConvert.DeserializeObject<DateTime>(json, new JsonSerializerSettings
        {
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            DateParseHandling = DateParseHandling.DateTime
        });

        now.ShouldBe(was);
        now.Kind.ShouldBe(DateTimeKind.Utc);
    }

    public static IEnumerable<string> Texts() =>
    [
        "plain", "", "città 日本", "<script>&'\"</script>", "a/b\\c", "line\nbreak\ttab\r", "\u0000\u001f", "\u2028\u2029",
        "slash/", "+plus"
    ];

    // Json.NET writes the hex digits of a control character's escape in lower case (\u001f), System.Text.Json in upper case
    private static string UpperHex(string json) =>
        System.Text.RegularExpressions.Regex.Replace(json, @"\\u[0-9a-fA-F]{4}", m => "\\u" + m.Value[2..].ToUpperInvariant());

    [TestCaseSource(nameof(Texts))]
    public void String_IsWrittenWithoutHtmlOrUnicodeEscaping_LikeJsonNet(string value) =>
        Now(value).ShouldBe(UpperHex(Was(value)));

    [Test]
    public void String_WithACharacterOutsideTheBasicPlane_IsWrittenAsItsSurrogatePair()
    {
        // The one byte-level difference from Json.NET (which wrote the UTF-8 bytes): System.Text.Json's encoder always
        // escapes a character above U+FFFF. A JSON parser reads both as the same character.
        Now("\U0001F600").ShouldBe("\"\\uD83D\\uDE00\"");
        JsonSerializer.Deserialize<string>(Now("\U0001F600"), Options).ShouldBe("\U0001F600");
    }

    public enum Plain { Zero, One, Two }

    [Flags]
    public enum Bits { None = 0, A = 1, B = 2, Both = 3 }

    [TestCase(Plain.Zero)]
    [TestCase(Plain.Two)]
    public void Enum_IsWrittenAsItsNumber_LikeJsonNetWithoutAConverter(Plain value) => Now(value).ShouldBe(Was(value));

    [Test]
    public void FlagsEnum_IsWrittenAsItsNumber() => Now(Bits.Both).ShouldBe("3");

    [TestCase("1", Plain.One)]
    [TestCase("\"One\"", Plain.One)]
    [TestCase("\"one\"", Plain.One)]
    [TestCase("\"TWO\"", Plain.Two)]
    [TestCase("\"2\"", Plain.Two)]
    [TestCase("7", (Plain)7)]
    public void Enum_IsReadFromANameInAnyCaseOrANumber(string json, Plain expected) =>
        JsonSerializer.Deserialize<Plain>(json, Options).ShouldBe(expected);

    [TestCase("\"Three\"")]
    [TestCase("true")]
    [TestCase("1.5")]
    public void Enum_RefusesWhatNamesNoMember(string json) =>
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<Plain>(json, Options));

    [Test]
    public void BuildState_IsWrittenAndReadByLibddbsLowercaseNames()
    {
        Now(BuildState.Pending).ShouldBe("\"pending\"");
        JsonSerializer.Deserialize<BuildState>("\"complete\"", Options).ShouldBe(BuildState.Complete);
    }

    [TestCase("true", true)]
    [TestCase("false", false)]
    [TestCase("\"true\"", true)]
    [TestCase("\"False\"", false)]
    [TestCase("1", true)]
    [TestCase("0", false)]
    public void Bool_IsReadLikeJsonNetReadsIt(string json, bool expected)
    {
        JsonSerializer.Deserialize<bool>(json, Options).ShouldBe(expected);
        JsonConvert.DeserializeObject<bool>(json).ShouldBe(expected);
    }

    [TestCase("\"yes\"")]
    [TestCase("null")]
    [TestCase("[]")]
    public void Bool_RefusesWhatJsonNetRefused(string json)
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<bool>(json, Options));
        Should.Throw<Newtonsoft.Json.JsonException>(() => JsonConvert.DeserializeObject<bool>(json));
    }

    [TestCase("\"text\"", "text")]
    [TestCase("12345", "12345")]
    [TestCase("1.5", "1.5")]
    [TestCase("true", "true")]
    [TestCase("false", "false")]
    public void String_IsReadFromAStringOrAPrimitive(string json, string expected) =>
        JsonSerializer.Deserialize<string>(json, Options).ShouldBe(expected);

    [TestCase("{}")]
    [TestCase("[]")]
    public void String_RefusesAnObjectOrAnArray(string json) =>
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<string>(json, Options));

    [Test]
    public void DictionaryKeys_AreNotRenamed_AndKeepTheirCase()
    {
        var value = new Dictionary<string, int> { ["PascalKey"] = 1, ["snake_key"] = 2 };

        Now(value).ShouldBe(Was(value));
        JsonSerializer.Deserialize<Dictionary<string, int>>("""{"A":1,"a":2}""", Options)!.Count.ShouldBe(2);
    }

    [Test]
    public void EpochSeconds_AreWrittenAsWholeOrFractionalSeconds()
    {
        var converter = new SecondEpochConverter();
        var options = new JsonSerializerOptions { Converters = { converter } };

        string Write(DateTime value) => JsonSerializer.Serialize(value, options);

        Write(DateTime.UnixEpoch).ShouldBe("0");
        Write(DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime).ShouldBe("1700000000");
        Write(DateTimeOffset.FromUnixTimeMilliseconds(1700000000500).UtcDateTime).ShouldBe("1700000000.5");
        Write(default).ShouldBe("-62135596800");
        JsonSerializer.Deserialize<DateTime>("1700000000", options).ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime);
        JsonSerializer.Deserialize<DateTime>("1700000000.5", options).ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1700000000500).UtcDateTime);
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<DateTime>("\"1700000000\"", options));
    }

    [TestCase(1761000000)]
    [TestCase(0)]
    [TestCase(1700000000)]
    public void UnixSeconds_AreJsonNetsUnixDateTimeConverter(long seconds)
    {
        var value = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        var options = new JsonSerializerOptions { Converters = { new UnixSecondsConverter() } };

        JsonSerializer.Serialize(value, options).ShouldBe(JsonConvert.SerializeObject(value, new UnixDateTimeConverter()));
        // The expiry of a token carries a fraction: truncated
        JsonSerializer.Serialize(value.AddMilliseconds(999), options)
            .ShouldBe(JsonConvert.SerializeObject(value.AddMilliseconds(999), new UnixDateTimeConverter()));
    }
}
