namespace Registry.Test.Common;

/// <summary>
/// The one test category taxonomy. Use these constants, never a string literal:
/// <c>[Category(TestCategories.Native)]</c>. Select with <c>dotnet test --filter "TestCategory=..."</c>.
/// <list type="bullet">
/// <item><see cref="Unit"/> and <see cref="Integration"/>: set once per assembly
/// (<c>[assembly: Category(...)]</c>): Registry.Web.UnitTest is Unit, Registry.Web.IntegrationTest
/// is Integration. The rule that decides between them is in Registry.Web.UnitTest.csproj.</item>
/// <item><see cref="Native"/>: the fixture calls native libddb (or a Program path that initialises it).</item>
/// <item><see cref="Network"/>: the fixture needs the network or a Docker daemon: test data downloads
/// (TestFS), container images (MariaDB, LDAP through Testcontainers) or a remote service.</item>
/// <item><see cref="Baseline"/>: a characterization test that pins existing behaviour; the
/// files live in Baselines/.</item>
/// </list>
/// </summary>
public static class TestCategories
{
    public const string Unit = "Unit";
    public const string Integration = "Integration";
    public const string Native = "Native";
    public const string Network = "Network";
    public const string Baseline = "Baseline";
}
