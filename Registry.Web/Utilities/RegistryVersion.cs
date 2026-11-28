using System.Linq;

namespace Registry.Web.Utilities;

/// <summary>The version of Registry, as the REST API and the Hub show it.</summary>
public static class RegistryVersion
{
    /// <summary>
    /// The assembly version as major.minor.patch (the format of AssemblyInfo.ts): <see cref="System.Version"/> holds
    /// four parts and would print the trailing revision.
    /// </summary>
    public static string? Current { get; } = Format(typeof(RegistryVersion).Assembly.GetName().Version?.ToString());

    private static string? Format(string? version) =>
        version == null ? null : string.Join(".", version.Split('.').Take(3));
}
