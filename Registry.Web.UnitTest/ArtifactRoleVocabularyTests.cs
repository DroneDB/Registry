#nullable enable
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Registry.Adapters.Testing;
using Registry.Ports.DroneDB;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The artifact roles are libddb's vocabulary (the entry types of the contract fixture list them): a role that
/// Registry or the Hub names and libddb does not define is a typo that no build ever matches, and the thumbnail,
/// the viewer or the download for it silently never appears.
/// </summary>
[TestFixture]
public sealed class ArtifactRoleVocabularyTests
{
    private const string HubRoles = "Registry.Web/ClientApp/webapp/js/libs/ddb/buildArtifacts.js";

    private static string[] LibddbRoles() => [.. ContractFixture.Load("entry-types").Output.AsArray()
        .SelectMany(t => t!["roles"]!.AsArray().Select(r => r!.GetValue<string>()))
        .Distinct()
        .Order()];

    [Test]
    public void TheFixture_ListsTheRolesOfEveryBuiltType()
    {
        LibddbRoles().ShouldContain(ArtifactRoles.RasterCog);
        LibddbRoles().ShouldContain(ArtifactRoles.GsplatLod);
        LibddbRoles().ShouldContain(ArtifactRoles.GsplatExport);
    }

    [Test]
    public void EveryRoleOfTheRegistry_IsARoleOfLibddb()
    {
        var registryRoles = typeof(ArtifactRoles).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        registryRoles.ShouldNotBeEmpty();
        registryRoles.ShouldBeSubsetOf(LibddbRoles());
    }

    [Test]
    public void EveryRoleOfTheHub_IsARoleOfLibddb()
    {
        var file = RepositoryFiles.PathOf(HubRoles);
        if (!File.Exists(file))
            Assert.Ignore("The Hub submodule is not checked out");

        // The members of the ArtifactRole enum: NAME: 'role.name'
        var enumBody = Regex.Match(File.ReadAllText(file), @"ArtifactRole\s*=\s*Object\.freeze\(\{(?<body>[^}]*)\}",
            RegexOptions.Singleline);
        enumBody.Success.ShouldBeTrue($"{HubRoles} declares ArtifactRole");
        var hubRoles = Regex.Matches(enumBody.Groups["body"].Value, @"^\s*[A-Z0-9_]+\s*:\s*'(?<role>[^']+)'",
                RegexOptions.Multiline)
            .Select(m => m.Groups["role"].Value)
            .ToArray();

        hubRoles.ShouldNotBeEmpty();
        hubRoles.ShouldBeSubsetOf(LibddbRoles());
    }
}
