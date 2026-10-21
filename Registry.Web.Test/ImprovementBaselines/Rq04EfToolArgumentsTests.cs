#nullable enable
using System;
using NUnit.Framework;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-04: <c>dotnet ef ... -- --provider MySql</c> must reach the design-time host on MySQL.
/// The old detection required exactly <c>--provider X --applicationName Y</c>; dotnet-ef 10 calls
/// <c>Program.CreateHostBuilder</c> with only the arguments after <c>--</c>, so the provider was
/// ignored and the Sqlite migrations were listed for the MySQL project.
/// <see cref="Program.TryGetEfToolProvider"/> now finds the option anywhere, in both forms.
/// Run: dotnet test Registry.Web.Test --filter "FullyQualifiedName~Rq04EfToolArgumentsTests"
/// </summary>
[TestFixture]
[Category("ImprovementBaseline")]
public class Rq04EfToolArgumentsTests
{
    [TestCase(new[] { "--provider", "MySql" }, "MySql", TestName = "dotnet-ef 10: only the arguments after --")]
    [TestCase(new[] { "--provider", "MySql", "--applicationName", "Registry.Web" }, "MySql",
        TestName = "legacy: provider then applicationName")]
    [TestCase(new[] { "--applicationName=Registry.Web", "--provider", "Sqlite" }, "Sqlite",
        TestName = "applicationName first, as one token")]
    [TestCase(new[] { "--AppSettings:MySqlServerVersion", "10.11.0-mariadb", "--provider=MySql" }, "MySql",
        TestName = "provider as --provider=value after another option")]
    [TestCase(new[] { "--PROVIDER", "MySql" }, "MySql", TestName = "option name is case-insensitive")]
    public void ProviderArgument_IsFoundAnywhere(string[] args, string expected)
    {
        Program.TryGetEfToolProvider(args, out var provider).ShouldBeTrue();
        provider.ShouldBe(expected);
    }

    private static readonly TestCaseData[] NotEfToolArguments =
    [
        new TestCaseData((object)Array.Empty<string>()).SetName("no arguments"),
        new TestCaseData((object)new[] { "--urls", "http://localhost:5000" }).SetName("web host arguments"),
        new TestCaseData((object)new[] { "--applicationName", "Registry.Web" }).SetName("applicationName without provider"),
        new TestCaseData((object)new[] { "--provider" }).SetName("provider without value"),
        new TestCaseData((object)new[] { "--provider", "--applicationName", "Registry.Web" })
            .SetName("provider followed by an option"),
        new TestCaseData((object)new[] { "--provider=" }).SetName("provider with an empty value")
    ];

    [TestCaseSource(nameof(NotEfToolArguments))]
    public void WithoutProviderValue_IsNotAnEfToolInvocation(string[] args)
    {
        Program.TryGetEfToolProvider(args, out var provider).ShouldBeFalse();
        provider.ShouldBeNull();
    }

    [Test]
    public void NullArguments_AreNotAnEfToolInvocation()
    {
        Program.TryGetEfToolProvider(null!, out var provider).ShouldBeFalse();
        provider.ShouldBeNull();
    }
}
