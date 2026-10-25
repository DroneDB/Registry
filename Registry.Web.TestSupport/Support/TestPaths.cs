#nullable enable
using Registry.Adapters.Testing;
using Registry.Web.Utilities;

namespace Registry.Web.Test.Support;

/// <summary>
/// The dataset path rule for tests: <see cref="DatasetPathPolicy"/> over
/// <see cref="FakeDdbWrapper"/>, whose <c>NormalizePath</c> follows libddb's golden path vectors, so a
/// unit test applies the production rule without loading libddb.
/// </summary>
public static class TestPaths
{
    public static DatasetPathPolicy Policy { get; } = new(new FakeDdbWrapper());
}
