using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Registry.Adapters.Testing;
using Shouldly;

namespace Registry.Web.Test.Contract;

/// <summary>
/// The libddb contract files vendored in Registry.Adapters.Testing/Contract are exactly the ones
/// Contract/SOURCE lists, byte for byte. A hand edit, a partial copy or a line-ending
/// conversion fails here; an update is deliberate: Registry.Adapters.Testing/update-contract.sh
/// copies the files from DroneDB at one commit and rewrites SOURCE.
/// </summary>
[TestFixture]
public sealed class ContractSourceTests
{
    private static readonly string[] VendoredFiles =
        ContractFiles.Paths.Where(p => p != ContractFiles.SourceFileName).ToArray();

    [Test]
    public void Source_NamesTheDroneDbCommit()
    {
        var source = ContractFiles.Source;

        source.Repository.ShouldBe("https://github.com/DroneDB/DroneDB");
        source.Path.ShouldBe("tests/contract");
        Regex.IsMatch(source.Commit, "^[0-9a-f]{40}$").ShouldBeTrue($"commit '{source.Commit}' is not a full SHA");
    }

    [Test]
    public void Source_ListsEveryVendoredFileAndNothingElse()
    {
        var listed = ContractFiles.Source.Hashes.Keys.Order().ToArray();

        listed.ShouldBe(VendoredFiles.Order().ToArray());
        listed.ShouldContain("README.md");
        listed.Count(p => p.StartsWith("fixtures/", System.StringComparison.Ordinal)).ShouldBeGreaterThan(0);
        listed.Count(p => p.StartsWith("schemas/", System.StringComparison.Ordinal)).ShouldBeGreaterThan(0);
    }

    [TestCaseSource(nameof(VendoredFiles))]
    public void VendoredFile_MatchesItsSourceHash(string path)
    {
        ContractFiles.Sha256(path).ShouldBe(ContractFiles.Source.Hashes[path],
            $"{path} differs from DroneDB {ContractFiles.Source.Commit}: run Registry.Adapters.Testing/update-contract.sh");
    }

    [Test]
    public void EveryFixture_NamesAVendoredSchema_AndEverySchemaHasAFixture()
    {
        var schemas = ContractFixture.Names.Select(n => ContractFixture.Load(n).Schema).ToHashSet();
        var vendoredSchemas = VendoredFiles.Where(p => p.StartsWith("schemas/", System.StringComparison.Ordinal))
            .Select(p => p["schemas/".Length..]).ToHashSet();

        schemas.ShouldBe(vendoredSchemas, ignoreOrder: true);
    }
}
