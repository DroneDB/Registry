using System.IO;
using NUnit.Framework;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Rules;

/// <summary>
/// The Hub runs the same input-rule test cases as the Registry from its own copy
/// (<c>ClientApp/webapp/js/libs/__vectors__/input-rules.json</c>), so a Hub-only checkout can test its rules; the copy
/// must stay the Registry's file, or the two sides test different cases.
/// </summary>
[TestFixture]
public sealed class HubInputRuleVectorsTests
{
    private const string HubCopy = "Registry.Web/ClientApp/webapp/js/libs/__vectors__/input-rules.json";
    private const string RegistryFile = "Registry.Web.UnitTest/" + InputRuleVectors.FileName;

    [Test]
    public void TheHubCopy_IsTheRegistryFile()
    {
        // Line endings may differ with the checkout's autocrlf setting
        static string Read(string path) => File.ReadAllText(RepositoryFiles.PathOf(path)).ReplaceLineEndings("\n");

        Read(HubCopy).ShouldBe(Read(RegistryFile),
            $"{HubCopy} must be a copy of {RegistryFile}: copy the Registry's file into the Hub");
    }
}
