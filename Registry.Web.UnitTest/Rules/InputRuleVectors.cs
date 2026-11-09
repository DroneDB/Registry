using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Registry.Web.Test.Rules;

/// <summary>
/// The test cases of the published input rules (<c>Rules/input-rule-vectors.json</c>), shared with the Hub, and the
/// way a client runs a published ECMAScript pattern.
/// </summary>
internal static class InputRuleVectors
{
    public const string FileName = "Rules/input-rule-vectors.json";

    private static readonly Lazy<JsonObject> Document = new(() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, FileName)))!.AsObject());

    /// <summary>A value of a rule with the server's and the client's answer.</summary>
    public sealed record Case(string Value, bool Server, bool Precheck)
    {
        // Control characters shown escaped, so a failure list stays readable
        public override string ToString() =>
            "'" + string.Concat(Value.Select(c => char.IsControl(c) ? $"\\u{(int)c:X4}" : c.ToString())) + "'";
    }

    /// <summary>The values of <paramref name="rule"/> under <paramref name="kind"/> ("accept", "reject").</summary>
    public static IReadOnlyList<string> Values(string rule, string kind) =>
        [.. Document.Value[rule]![kind]!.AsArray().Select(v => v!.GetValue<string>())];

    /// <summary>The cases of <paramref name="rule"/>, each with its "server" and "precheck" answers.</summary>
    public static IReadOnlyList<Case> Cases(string rule, string valueMember) =>
    [
        .. Document.Value[rule]!.AsArray().Select(v => new Case(v![valueMember]!.GetValue<string>(),
            v["server"]!.GetValue<bool>(), v["precheck"]!.GetValue<bool>()))
    ];

    /// <summary>Whether a client's <c>new RegExp(pattern).test(value)</c> is true.</summary>
    public static bool Matches(string pattern, string value) =>
        Regex.IsMatch(value, pattern, RegexOptions.ECMAScript);
}
