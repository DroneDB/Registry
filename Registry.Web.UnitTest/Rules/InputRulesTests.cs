using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Registry.Adapters.Testing;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Models.Email;
using Registry.Web.Services.Features;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.Rules;

/// <summary>
/// FR-XC-12: each input rule has one definition in the Registry, and <c>GET /sys/features</c> publishes it for the
/// Hub (<see cref="InputRules"/>). The shared test cases (<see cref="InputRuleVectors"/>) are run against the server's
/// own check and against the published rule as a client runs it: where they must agree, they do; where the pre-check
/// is looser (an e-mail form, an alias of '.ddb'), it never refuses what the server accepts.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~InputRules"
/// </summary>
[TestFixture]
public class InputRulesTests
{
    private static readonly InputRulesDto Published = InputRules.Describe();

    private static readonly DatasetPathPolicy Paths = new(new FakeDdbWrapper());

    private static void NoFailures(IEnumerable<string> failures) => failures.ToList().ShouldBeEmpty();

    #region Slugs

    [Test]
    public void Slug_ThePatternAndSlugRules_AgreeOnEveryCase()
    {
        var pattern = Published.Slug!.Pattern!;
        var failures = new List<string>();
        foreach (var slug in InputRuleVectors.Values("slug", "accept"))
            if (!SlugRules.IsValid(slug) || !InputRuleVectors.Matches(pattern, slug))
                failures.Add($"accept '{slug}': server {SlugRules.IsValid(slug)}, pattern {InputRuleVectors.Matches(pattern, slug)}");
        foreach (var slug in InputRuleVectors.Values("slug", "reject"))
            if (SlugRules.IsValid(slug) || InputRuleVectors.Matches(pattern, slug))
                failures.Add($"reject '{slug}': server {SlugRules.IsValid(slug)}, pattern {InputRuleVectors.Matches(pattern, slug)}");

        NoFailures(failures);
    }

    [Test]
    public void Slug_ThePublishedLimits_AreSlugRules_AndThePatternKeepsThem()
    {
        Published.Slug!.MinLength.ShouldBe(SlugRules.MinLength);
        Published.Slug.MaxLength.ShouldBe(SlugRules.MaxLength);
        Published.Slug.Description.ShouldBe(SlugRules.Description);

        var pattern = Published.Slug.Pattern!;
        InputRuleVectors.Matches(pattern, new string('a', SlugRules.MinLength - 1)).ShouldBeFalse();
        InputRuleVectors.Matches(pattern, new string('a', SlugRules.MinLength)).ShouldBeTrue();
        InputRuleVectors.Matches(pattern, new string('a', SlugRules.MaxLength)).ShouldBeTrue();
        InputRuleVectors.Matches(pattern, new string('a', SlugRules.MaxLength + 1)).ShouldBeFalse();
    }

    #endregion

    #region User and role names (D-007, D-015)

    [Test]
    public void Name_ThePatternAndNameRules_AgreeOnEveryCase()
    {
        var pattern = Published.Name!.Pattern!;
        var failures = new List<string>();
        foreach (var name in InputRuleVectors.Values("name", "accept"))
            if (!NameRules.IsValid(name) || !InputRuleVectors.Matches(pattern, name))
                failures.Add($"accept '{name}': server {NameRules.IsValid(name)}, pattern {InputRuleVectors.Matches(pattern, name)}");
        foreach (var name in InputRuleVectors.Values("name", "reject"))
            if (NameRules.IsValid(name) || InputRuleVectors.Matches(pattern, name))
                failures.Add($"reject '{name}': server {NameRules.IsValid(name)}, pattern {InputRuleVectors.Matches(pattern, name)}");

        NoFailures(failures);
    }

    [Test]
    public void Name_ThePublishedRule_IsNameRules_AndItsLimitsAre1To127()
    {
        Published.Name!.MinLength.ShouldBe(1);
        Published.Name.MaxLength.ShouldBe(127);
        Published.Name.Description.ShouldBe("1 to 127 characters: lowercase letters (a–z) and digits (0–9)");
        Published.Name.Pattern.ShouldBe("^[a-z0-9]{1,127}$");

        var pattern = Published.Name.Pattern!;
        InputRuleVectors.Matches(pattern, "").ShouldBeFalse();
        InputRuleVectors.Matches(pattern, new string('a', 127)).ShouldBeTrue();
        InputRuleVectors.Matches(pattern, new string('a', 128)).ShouldBeFalse();
        NameRules.IsValid(new string('a', 127)).ShouldBeTrue();
        NameRules.IsValid(new string('a', 128)).ShouldBeFalse();
        NameRules.InvalidMessage("User name")
            .ShouldBe("User name is invalid: use 1 to 127 characters: lowercase letters (a–z) and digits (0–9)");
    }

    // The final line feed that .NET's '$' skips: the check is the characters, whatever the engine
    [TestCase("bob\n")]
    [TestCase("\nbob")]
    [TestCase("")]
    [TestCase(null)]
    public void Name_IsValid_RefusesWhatARegexAnchorCouldLetThrough(string? name) => NameRules.IsValid(name).ShouldBeFalse();

    #endregion

    #region Timespan (what .NET TimeSpan.Parse accepts, invariant culture)

    [Test]
    public void Timespan_TheRecordedAnswers_AreThoseOfTimeSpanTryParse()
    {
        var failures = new List<string>();
        foreach (var c in InputRuleVectors.Cases("timespan", "value"))
        {
            var server = TimeSpan.TryParse(c.Value, System.Globalization.CultureInfo.InvariantCulture, out _);
            if (server != c.Server)
                failures.Add($"{c}: TimeSpan.TryParse {server}, recorded {c.Server}");
            if (c.Precheck != c.Server)
                failures.Add($"{c}: the Hub answers {c.Precheck}, TimeSpan.TryParse {c.Server}: the Hub rule is TimeSpan's");
        }

        NoFailures(failures);
    }

    // The settings are read by the configuration binder, which converts a text with TimeSpan.Parse and the invariant
    // culture (FR-ADM-06). These are the values that parser gives (the literals are what it answered), among them the two
    // forms that surprise: hours are at most 23 in "hh:mm", and three numbers whose first is above 23 are days:hours:minutes.
    [TestCase("24:00", null)]
    [TestCase("48:00", null)]
    [TestCase("100:00", null)]
    [TestCase("1.24:00", null)]
    [TestCase("23:59", "23:59:00")]
    [TestCase("24:00:00", "24.00:00:00")]
    [TestCase("25:00:00", "25.00:00:00")]
    [TestCase("24:00:01", "24.00:01:00")]
    [TestCase("24:01:30", "24.01:30:00")]
    [TestCase("24:00:00.5", null)]
    [TestCase("24:30:00", null)]
    [TestCase("1:2:3", "01:02:03")]
    [TestCase("1:00:00:00", "1.00:00:00")]
    [TestCase("1.00:00", "1.00:00:00")]
    [TestCase("30", "30.00:00:00")]
    [TestCase("00:00:.5", "00:00:00.5000000")]
    [TestCase("10675199:02:48", "10675199.02:48:00")]
    [TestCase("10675199:02:49", null)]
    public void Timespan_WhatTheParserReads_IsAsRecorded(string text, string? expected)
    {
        var ok = TimeSpan.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var parsed);

        ok.ShouldBe(expected is not null, text);
        if (expected is not null)
            parsed.ToString("c", System.Globalization.CultureInfo.InvariantCulture).ShouldBe(expected, text);
    }

    // The same parser through the configuration binder, the way the Registry reads an appsettings value
    [Test]
    public void Timespan_TheConfigurationBinder_ReadsTheSettingsAsTimeSpanParseDoes()
    {
        TimeSpan? Bind(string text)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?> { ["T"] = text })
                .Build();
            try { return configuration.GetValue<TimeSpan?>("T"); }
            catch (InvalidOperationException) { return null; }
        }

        Bind("24:00:00").ShouldBe(TimeSpan.FromDays(24));
        Bind("25:00:00").ShouldBe(TimeSpan.FromDays(25));
        Bind("23:59").ShouldBe(new TimeSpan(23, 59, 0));
        Bind("1.00:00:00").ShouldBe(TimeSpan.FromDays(1));
        Bind("24:00").ShouldBeNull();
        Bind("48:00").ShouldBeNull();
    }

    #endregion

    #region E-mail

    [Test]
    public void Email_TheServerAndThePreCheck_AnswerAsRecorded()
    {
        var pattern = Published.Email!.Pattern!;
        var failures = new List<string>();
        foreach (var c in InputRuleVectors.Cases("email", "value"))
        {
            if (EmailAddress.IsValid(c.Value) != c.Server)
                failures.Add($"{c}: server {EmailAddress.IsValid(c.Value)}, recorded {c.Server}");
            if (InputRuleVectors.Matches(pattern, c.Value.Trim()) != c.Precheck)
                failures.Add($"{c}: pre-check {!c.Precheck}, recorded {c.Precheck}");
        }

        NoFailures(failures);
    }

    [Test]
    public void Email_ThePreCheck_NeverRefusesAnAddressTheServerAccepts()
    {
        var pattern = Published.Email!.Pattern!;
        NoFailures(InputRuleVectors.Cases("email", "value")
            .Where(c => EmailAddress.IsValid(c.Value) && !InputRuleVectors.Matches(pattern, c.Value.Trim()))
            .Select(c => c.ToString()));
    }

    [Test]
    public void Email_TheRefusalMessage_IsPublished() =>
        Published.Email!.Message.ShouldBe(InputRules.InvalidEmailMessage);

    #endregion

    #region Entry names

    // What a client answers with the published rule: the pattern, then the reserved names
    private static bool PreCheck(EntryNameRuleDto rule, string name, bool atRoot) =>
        InputRuleVectors.Matches(rule.Pattern!, name) &&
        !rule.ReservedNames!.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase)) &&
        !(atRoot && rule.RootReservedNames!.Contains(name, StringComparer.Ordinal));

    [TestCase("entryName", "")]
    [TestCase("entryNameInFolder", "sub")]
    public void EntryName_TheServerAndThePreCheck_AnswerAsRecorded(string rule, string folder)
    {
        var failures = new List<string>();
        foreach (var c in InputRuleVectors.Cases(rule, "name"))
        {
            var server = Paths.IsEntryName(c.Value, folder);
            var preCheck = PreCheck(Published.EntryName!, c.Value, folder.Length == 0);
            if (server != c.Server)
                failures.Add($"{c}: server {server}, recorded {c.Server}");
            if (preCheck != c.Precheck)
                failures.Add($"{c}: pre-check {preCheck}, recorded {c.Precheck}");
        }

        NoFailures(failures);
    }

    [Test]
    public void EntryName_ThePreCheck_NeverRefusesANameTheServerAccepts() =>
        NoFailures(InputRuleVectors.Cases("entryName", "name")
            .Where(c => Paths.IsEntryName(c.Value) && !PreCheck(Published.EntryName!, c.Value, atRoot: true))
            .Select(c => c.ToString()));

    [Test]
    public void EntryName_TheReservedNames_AreLibddbsAndDatasetPathPolicys()
    {
        Published.EntryName!.ReservedNames.ShouldBe([".ddb"]);
        Published.EntryName.RootReservedNames.ShouldBe(DatasetPathPolicy.RootReservedNames);
        Published.EntryName.RootReservedNames.ShouldBe([".uploads"]);
    }

    #endregion

    #region The "already exists" message (D-040)

    [Test]
    public void NameClash_IsTheOneMessage_ComparedWithoutRegardToCase()
    {
        Published.NameClash!.Message.ShouldBe("A file or folder named '{name}' already exists in this folder");
        Published.NameClash.CaseInsensitive.ShouldBeTrue();
        InputRules.NameClashMessage("odm_orthophoto.tif")
            .ShouldBe("A file or folder named 'odm_orthophoto.tif' already exists in this folder");
    }

    #endregion

    #region Cron words

    [Test]
    public void Cron_TheDisableWords_AreCronSettings_InAnyCase()
    {
        Published.Cron!.DisableWords.ShouldBe(["disabled", "off", "none"]);
        Published.Cron.DisableWords.ShouldBe(CronSetting.DisableWords);
        Published.Cron.MinFields.ShouldBe(5);
        Published.Cron.MaxFields.ShouldBe(6);
        Published.Cron.Description.ShouldBe(
            "leave it empty for the default schedule; 'disabled', 'off' or 'none' (any case) turns the job off; " +
            "a macro such as '@daily' is a schedule; any other value has 5 or 6 fields separated by spaces");
        Published.Cron.Macros.ShouldBe(CronSetting.Macros);
        Published.Cron.Macros.ShouldBe(
            ["@yearly", "@annually", "@monthly", "@weekly", "@daily", "@midnight", "@hourly", "@every_minute", "@every_second"]);
        foreach (var word in Published.Cron.DisableWords!)
        {
            CronSetting.IsDisabled(word).ShouldBeTrue(word);
            CronSetting.IsDisabled(" " + word.ToUpperInvariant() + " ").ShouldBeTrue(word);
            CronSetting.Resolve(word, "0 0 * * *").ShouldBeNull(word);
        }

        CronSetting.IsDisabled("0 3 * * *").ShouldBeFalse();
        CronSetting.IsDisabled(null).ShouldBeFalse();
    }

    #endregion

    #region Latin letter rule (D-035)

    // What a client answers with the published rule: the normalized name contains the pattern
    private static bool PreCheck(LatinNameRuleDto rule, string name) =>
        InputRuleVectors.Matches(rule.Pattern!, name.Normalize(rule.Normalization switch
        {
            "NFC" => NormalizationForm.FormC,
            "NFD" => NormalizationForm.FormD,
            "NFKC" => NormalizationForm.FormKC,
            "NFKD" => NormalizationForm.FormKD,
            _ => throw new ArgumentException($"Unknown normalization '{rule.Normalization}'")
        }));

    [Test]
    public void LatinName_TheServerAndThePreCheck_AgreeOnEveryCase()
    {
        var rule = Published.LatinName!;
        var failures = new List<string>();
        foreach (var name in InputRuleVectors.Values("latinName", "accept"))
            if (!InputRules.HasLatinLetterOrDigit(name) || !PreCheck(rule, name))
                failures.Add($"accept '{name}'");
        foreach (var name in InputRuleVectors.Values("latinName", "reject"))
            if (InputRules.HasLatinLetterOrDigit(name) || PreCheck(rule, name))
                failures.Add($"reject '{name}'");

        NoFailures(failures);
    }

    [Test]
    public void LatinName_TheMessages_AreThoseOfD035()
    {
        var rule = Published.LatinName!;
        rule.Normalization.ShouldBe("NFD");
        rule.Message.ShouldBe("Use at least one Latin letter (a–z) or digit in the name, or enter a slug");
        rule.MessageWithoutSlug.ShouldBe("Use at least one Latin letter (a–z) or digit");
    }

    // A name the rule accepts keeps a Latin letter or digit in the slug made from it (the generator's own work)
    [Test]
    public void LatinName_AnAcceptedName_KeepsALetterOrDigitInItsSlug()
    {
        foreach (var name in InputRuleVectors.Values("latinName", "accept"))
            name.ToSlug().Trim('0', '-', '_').ShouldNotBeEmpty(name);
    }

    #endregion

    #region GET /sys/features

    [Test]
    public async Task Features_PublishTheRules_AndBothSystemRoles()
    {
        var features = new FeaturesDto();
        await new InputRuleFeatures().FillAsync(features);

        features.SystemRoles.ShouldBe(["admin", "deactivated"]);
        features.InputRules.ShouldNotBeNull();
        features.InputRules.Slug!.Pattern.ShouldBe(InputRules.SlugPattern);
        features.InputRules.Email!.Pattern.ShouldBe(InputRules.EmailPattern);
        features.InputRules.EntryName!.Pattern.ShouldBe(InputRules.EntryNamePattern);
        features.InputRules.NameClash!.Message.ShouldBe(InputRules.NameClashMessageTemplate);
        features.InputRules.Cron!.DisableWords.ShouldBe(CronSetting.DisableWords);
        features.InputRules.LatinName!.Pattern.ShouldBe(InputRules.LatinNamePattern);
    }

    [TestCase("admin")]
    [TestCase("Admin")]
    [TestCase("deactivated")]
    [TestCase("DEACTIVATED")]
    public void SystemRoles_AreRecognisedInAnyCase(string role) => SystemRoles.IsSystemRole(role).ShouldBeTrue();

    [TestCase("user")]
    [TestCase("admins")]
    [TestCase("")]
    [TestCase(null)]
    public void SystemRoles_OtherNamesAreNot(string? role) => SystemRoles.IsSystemRole(role).ShouldBeFalse();

    #endregion
}
