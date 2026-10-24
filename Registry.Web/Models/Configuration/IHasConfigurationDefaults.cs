#nullable enable
namespace Registry.Web.Models.Configuration;

/// <summary>
/// Options whose defaults depend on the configured values (an empty list or path takes its default).
/// <see cref="ApplyDefaults"/> is the one place those defaults are set: the options pipeline runs it as
/// a <c>PostConfigure</c>, and the registration-time read (<c>GetRegistryOptions</c>) runs it too.
/// It must be idempotent.
/// </summary>
public interface IHasConfigurationDefaults
{
    void ApplyDefaults();
}

/// <summary>Reads of options whose nested sections the view may find missing or written as <c>null</c>.</summary>
public static class ConfigurationDefaultsExtensions
{
    /// <summary><paramref name="options"/> after <see cref="IHasConfigurationDefaults.ApplyDefaults"/>, so a missing nested section is its defaults.</summary>
    public static T WithDefaults<T>(this T options) where T : class, IHasConfigurationDefaults
    {
        options.ApplyDefaults();
        return options;
    }
}
