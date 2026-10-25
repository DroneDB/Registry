#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Hosting;
using Registry.Web.Models.Configuration;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// Every configuration a deployment has today binds into the focused options
/// classes and passes their validation, unchanged. The classes bind the same keys as <see cref="AppSettings"/>
/// (the <c>AppSettings</c> section and its subsections), so <c>appsettings.json</c>, <c>AppSettings__…</c>
/// environment variables (the Docker style) and command-line values keep working; no key was renamed.
/// <list type="bullet">
/// <item>the settings files shipped in the repository (the embedded defaults a first start writes, the
/// production template rendered as <c>run.sh</c> renders it, the testing compose settings) bind and
/// validate for the roles they are used with;</item>
/// <item>every key of those files is a property of exactly one focused class, and every <see cref="AppSettings"/>
/// property (the test fixture that holds the whole section) is bound by exactly one, to the same value;</item>
/// <item>Docker-style environment variables reach the focused classes;</item>
/// <item>each rule of the removed <c>Program.VerifySettings</c> is a validator rule, and nothing else is.</item>
/// </list>
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~ConfigurationCompatibilityTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class ConfigurationCompatibilityTests
{
    /// <summary>The variables <c>docker/production/run.sh</c> substitutes into the template (its defaults).</summary>
    private static readonly Dictionary<string, string> TemplateVariables = new()
    {
        ["REGISTRY_SECRET"] = "e7er2yjacmbqjxsmf6h3rtrh7t6wjhef7bkv6kauv3wng3jb3t5hx7jtjry5z2ydd6utbufgq6jar2v3cvexhcescgzac",
        ["REGISTRY_ADMIN_MAIL"] = "test@test.it",
        ["REGISTRY_ADMIN_PASSWORD"] = "password",
        ["EXTERNAL_URL"] = "",
        ["MYSQL_PASSWORD"] = "default-mysql-password",
        ["CONTROL_SWITCH"] = "$controlSwitch"
    };

    private static readonly InstanceType[] AllRoles = Enum.GetValues<InstanceType>();

    private static readonly InstanceType[] InMemoryJobStorageRoles =
        [InstanceType.Default, InstanceType.WebServer, InstanceType.ThumbnailGenerator];

    public static IEnumerable<TestCaseData> ShippedSettings()
    {
        yield return new TestCaseData("embedded appsettings-default.json", InMemoryJobStorageRoles)
            .SetArgDisplayNames("defaults");
        yield return new TestCaseData("docker/production/appsettings-template.json", AllRoles)
            .SetArgDisplayNames("production-template");
        yield return new TestCaseData("docker/testing/appsettings-testing.json", AllRoles)
            .SetArgDisplayNames("testing-compose");
    }

    [TestCaseSource(nameof(ShippedSettings))]
    public void ShippedSettings_BindAndValidate_ForTheirRoles(string source, InstanceType[] roles)
    {
        var configuration = Configuration(Load(source));

        foreach (var role in roles)
            RegistryOptions.Validate(configuration, role).ShouldBeEmpty($"{source} as {role}");
    }

    /// <summary>A first-start file has an empty secret; the start generates it before any host runs (in memory here, as --check does).</summary>
    [Test]
    public void DefaultSettings_WithoutTheGeneratedSecrets_FailOnlyOnThem()
    {
        var settings = Load("embedded appsettings-default.json");
        settings["AppSettings"]!["Secret"] = "";

        var configuration = new ConfigurationBuilder().AddJsonStream(Stream(settings)).Build();

        RegistryOptions.Validate(configuration, InstanceType.WebServer)
            .ShouldBe(["AppSettings:Secret is empty (the first start of a storage folder generates it)"]);
    }

    [TestCaseSource(nameof(ShippedSettings))]
    public void EveryAppSettingsKey_IsBoundByExactlyOneFocusedClass_WithTheSameValue(string source, InstanceType[] _)
    {
        var configuration = Configuration(Load(source));
        var whole = configuration.GetSection(AppSettings.SectionName).Get<AppSettings>()!;

        foreach (var property in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var owners = Owners(property).ToList();
            owners.Count.ShouldBe(1, $"AppSettings:{property.Name} must be bound by exactly one focused class, found " +
                                     string.Join(", ", owners.Select(o => o.Name)));

            var owner = owners[0];
            var expected = Json(property.GetValue(whole));
            var actual = RegistryOptions.Sections[owner] == AppSettings.SectionName
                ? Json(owner.GetProperty(property.Name)!.GetValue(Bind(configuration, owner)))
                : Json(Bind(configuration, owner));

            // A nested section that is absent is null in AppSettings and its defaults in the focused class
            if (expected == "null" && actual != "null" && property.PropertyType.IsClass &&
                property.PropertyType != typeof(string) && !property.PropertyType.IsArray)
                expected = Json(Activator.CreateInstance(property.PropertyType));

            actual.ShouldBe(expected, $"AppSettings:{property.Name} ({owner.Name}, {source})");
        }
    }

    /// <summary>
    /// The keys of the files themselves, not of a class: every key under <c>AppSettings</c> in the shipped settings is a
    /// property of exactly one focused class (a nested section is the class of that section), so a key a deployment
    /// has today is never silently ignored.
    /// </summary>
    [TestCaseSource(nameof(ShippedSettings))]
    public void EveryKeyOfTheShippedSettings_IsAPropertyOfExactlyOneFocusedClass(string source, InstanceType[] _)
    {
        var section = Load(source)[RegistryOptions.SectionName]!.AsObject();

        foreach (var (key, value) in section)
        {
            var owners = RegistryOptions.Sections
                .Where(entry => entry.Value == RegistryOptions.SectionName + ":" + key ||
                                (entry.Value == RegistryOptions.SectionName && HasProperty(entry.Key, key)))
                .Select(entry => entry.Key)
                .ToList();

            owners.Count.ShouldBe(1, $"AppSettings:{key} ({source}) must be bound by exactly one focused class, found " +
                                     string.Join(", ", owners.Select(o => o.Name)));

            if (value is JsonObject nested && RegistryOptions.Sections[owners[0]] != RegistryOptions.SectionName)
                foreach (var (name, _) in nested)
                    HasProperty(owners[0], name).ShouldBeTrue($"AppSettings:{key}:{name} ({source}) is not a property of {owners[0].Name}");
        }
    }

    /// <summary>
    /// The production template used to carry <c>ChunkedUploadSessionTimeout</c>, which nothing reads: a deployment whose
    /// file still has it (or any key this version does not know) binds to exactly the options the template does, and
    /// passes validation for every role.
    /// </summary>
    [Test]
    public void AKeyNoClassBinds_IsIgnored_ByEveryFocusedClass()
    {
        // The same generated secrets in both, so that what differs is the key
        var template = Load("docker/production/appsettings-template.json");
        SettingsFile.AddMissingSecrets(template);
        var older = template.DeepClone().AsObject();
        older[RegistryOptions.SectionName]!["ChunkedUploadSessionTimeout"] = "01:00:00";

        var current = Configuration(template);
        var withKey = Configuration(older);

        foreach (var options in RegistryOptions.Sections.Keys)
            Json(Bind(withKey, options)).ShouldBe(Json(Bind(current, options)), options.Name);
        foreach (var role in AllRoles)
            RegistryOptions.Validate(withKey, role).ShouldBeEmpty($"the older template as {role}");
    }

    private static bool HasProperty(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) != null;

    /// <summary>The Docker style: <c>AppSettings__…</c> environment variables override the file, in every focused class.</summary>
    [Test]
    public void DockerStyleEnvironmentVariables_ReachTheFocusedClasses()
    {
        const string prefix = "COMPAT_";
        var variables = new Dictionary<string, string>
        {
            ["AppSettings__ProcessingBudget__HeavyWorkers"] = "3",
            ["AppSettings__HostBudget__ProcessingNodes"] = "2",
            ["AppSettings__KnownNetworks__0"] = "172.16.0.0/12",
            ["AppSettings__ExternalUrlOverride"] = "https://hub.example.org",
            ["AppSettings__DatasetsPath"] = "/data/datasets",
            ["AppSettings__Import__MaxRedirects"] = "2",
            ["AppSettings__HangfireJobRetentionDays"] = "5",
            ["ConnectionStrings__RegistryConnection"] = "Server=db;Database=RegistryData"
        };

        try
        {
            foreach (var (name, value) in variables)
                Environment.SetEnvironmentVariable(prefix + name, value);

            var configuration = new ConfigurationBuilder()
                .AddJsonStream(Stream(Load("docker/production/appsettings-template.json", render: true)))
                .AddEnvironmentVariables(prefix)
                .Build();

            configuration.GetRegistryOptions<HostResourceOptions>().ProcessingBudget.HeavyWorkers.ShouldBe(3);
            configuration.GetRegistryOptions<HostResourceOptions>().HostBudget.ProcessingNodes.ShouldBe(2);
            configuration.GetRegistryOptions<HttpHostOptions>().KnownNetworks.ShouldBe(["172.16.0.0/12"]);
            configuration.GetRegistryOptions<HttpHostOptions>().ExternalUrlOverride.ShouldBe("https://hub.example.org");
            configuration.GetRegistryOptions<StorageOptions>().DatasetsPath.ShouldBe("/data/datasets");
            configuration.GetRegistryOptions<ImportSettings>().MaxRedirects.ShouldBe(2);
            configuration.GetRegistryOptions<JobScheduleOptions>().HangfireJobRetentionDays.ShouldBe(5);
            configuration.GetConnectionString("RegistryConnection").ShouldBe("Server=db;Database=RegistryData");
            RegistryOptions.Validate(configuration, InstanceType.ProcessingNode).ShouldBeEmpty();
        }
        finally
        {
            foreach (var name in variables.Keys)
                Environment.SetEnvironmentVariable(prefix + name, null);
        }
    }

    /// <summary>Defaults that the code applies once (PostConfigure), where the old code filled them in or each consumer did.</summary>
    [Test]
    public void Defaults_AreAppliedOnce_WhenTheKeysAreMissing()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

        configuration.GetRegistryOptions<StorageOptions>().TempPath.ShouldBe(StorageOptions.DefaultTempPath);
        configuration.GetRegistryOptions<HttpHostOptions>().KnownProxies.ShouldBe(HttpHostOptions.DefaultKnownProxies);
        configuration.GetRegistryOptions<ThumbnailOptions>().DatasetThumbnailCandidates
            .ShouldBe(ThumbnailOptions.DefaultDatasetThumbnailCandidates);
        configuration.GetRegistryOptions<ImportSettings>().ShouldNotBeNull();
        configuration.GetRegistryOptions<UploadSettings>().QuotaWindowSeconds.ShouldBe(new UploadSettings().QuotaWindowSeconds);
        configuration.GetRegistryOptions<HostResourceOptions>().NativeConcurrency.ShouldNotBeNull();
    }

    /// <summary>The 18 rules of the removed <c>Program.VerifySettings</c>, each now a validator rule naming its key.</summary>
    [TestCase("AppSettings:ExternalUrlOverride", "not a url", "ExternalUrlOverride")]
    [TestCase("AppSettings:TokenExpirationInDays", "0", "TokenExpirationInDays")]
    [TestCase("AppSettings:DefaultAdmin:Email", null, "DefaultAdmin")]
    [TestCase("AppSettings:DatasetsPath", "", "DatasetsPath")]
    [TestCase("AppSettings:CachePath", " ", "CachePath")]
    [TestCase("AppSettings:BatchTokenLength", "7", "BatchTokenLength")]
    [TestCase("AppSettings:UploadBatchTimeout", "00:00:30", "UploadBatchTimeout")]
    [TestCase("AppSettings:RandomDatasetNameLength", "7", "RandomDatasetNameLength")]
    [TestCase("AppSettings:AuthCookieName", "", "AuthCookieName")]
    [TestCase("AppSettings:ThumbnailsCacheExpiration", "00:00:59", "ThumbnailsCacheExpiration")]
    [TestCase("AppSettings:TilesCacheExpiration", "00:00:59", "TilesCacheExpiration")]
    [TestCase("AppSettings:DatasetVisibilityCacheExpiration", "00:00:59", "DatasetVisibilityCacheExpiration")]
    [TestCase("AppSettings:ClearCacheInterval", "00:00:59", "ClearCacheInterval")]
    public void VerifySettingsRule_IsAValidatorRule(string key, string? value, string reported)
    {
        var settings = Values(Load("docker/testing/appsettings-testing.json"));
        settings[key] = value;
        if (value == null)
            settings.Remove(key);

        var errors = RegistryOptions.Validate(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            InstanceType.WebServer);
        errors.ShouldHaveSingleItem($"{string.Join(" | ", errors)}").ShouldContain(reported);
    }

    [TestCase("ExternalAuthUrl", "https://auth.example.org", "Cannot enable both LDAP")]
    [TestCase("LdapSettings:Server", "", "LdapSettings:Server")]
    [TestCase("LdapSettings:BaseDn", "", "LdapSettings:BaseDn")]
    [TestCase("LdapSettings:SearchFilter", "(uid=user)", "LdapSettings:SearchFilter")]
    public void VerifySettingsLdapRule_IsAValidatorRule(string key, string value, string reported)
    {
        var settings = Values(Load("docker/testing/appsettings-testing.json"));
        settings["AppSettings:LdapSettings:Enabled"] = "true";
        settings["AppSettings:LdapSettings:Server"] = "ldap.example.org";
        settings["AppSettings:LdapSettings:BaseDn"] = "dc=example,dc=org";
        settings["AppSettings:LdapSettings:SearchFilter"] = "(uid={0})";
        settings["AppSettings:" + key] = value;

        RegistryOptions.Validate(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), InstanceType.WebServer)
            .ShouldHaveSingleItem().ShouldContain(reported);
    }

    /// <summary>The checks that moved from elsewhere into validators, per role.</summary>
    [Test]
    public void RoleRules_ThumbnailTokenAndNodeJobStorage()
    {
        var settings = Values(Load("embedded appsettings-default.json"));
        settings["AppSettings:Secret"] = "configurationcompatibility";
        settings["AppSettings:ThumbnailGeneratorToken"] = "";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        RegistryOptions.Validate(configuration, InstanceType.WebServer).ShouldBeEmpty();
        RegistryOptions.Validate(configuration, InstanceType.ThumbnailGenerator)
            .ShouldHaveSingleItem().ShouldContain("ThumbnailGeneratorToken");
        RegistryOptions.Validate(configuration, InstanceType.ProcessingNode)
            .ShouldHaveSingleItem().ShouldContain("InMemory is not supported for a processing node");
    }

    private static IEnumerable<Type> Owners(PropertyInfo property) =>
        RegistryOptions.Sections
            .Where(entry => entry.Value == AppSettings.SectionName
                ? entry.Key.GetProperty(property.Name) != null
                : entry.Value == AppSettings.SectionName + ":" + property.Name)
            .Select(entry => entry.Key);

    /// <summary>The class as the binder fills it, before the defaults that depend on values (PostConfigure).</summary>
    private static object Bind(IConfiguration configuration, Type options) =>
        configuration.GetSection(RegistryOptions.Sections[options]).Get(options) ?? Activator.CreateInstance(options)!;

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    /// <summary>The configuration a host reads: the file with the secrets a start generates (in memory).</summary>
    private static IConfiguration Configuration(JsonObject settings)
    {
        SettingsFile.AddMissingSecrets(settings);
        return new ConfigurationBuilder().AddJsonStream(Stream(settings)).Build();
    }

    private static JsonObject Load(string source, bool render = true)
    {
        if (source.StartsWith("embedded ", StringComparison.Ordinal))
            return Parse(SettingsFile.DefaultContent());

        Assume.That(RepositoryFiles.Root, Is.Not.Null, "repository sources not available next to the test output");
        var text = File.ReadAllText(RepositoryFiles.PathOf(source));
        if (render)
            text = TemplateVariables.Aggregate(text, (current, variable) => current.Replace("$" + variable.Key, variable.Value));
        return Parse(text);
    }

    private static JsonObject Parse(string text) =>
        JsonNode.Parse(text, documentOptions: SettingsFile.DocumentOptions)!.AsObject();

    private static Dictionary<string, string?> Values(JsonObject settings) =>
        new ConfigurationBuilder().AddJsonStream(Stream(settings)).Build()
            .AsEnumerable().Where(pair => pair.Value != null)
            .ToDictionary(pair => pair.Key, pair => pair.Value);

    private static MemoryStream Stream(JsonObject settings) => new(Encoding.UTF8.GetBytes(settings.ToJsonString()));
}
