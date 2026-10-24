#nullable enable
namespace Registry.Web.Models.Configuration;

/// <summary>The defaults of configuration-bound lists.</summary>
public static class ConfigurationLists
{
    /// <summary>
    /// The one rule for the defaults of a configuration-bound list: the configured list, or
    /// <paramref name="defaults"/> when it is not set (null or empty, as an empty JSON array binds to no
    /// item). Such a list must have no initializer: the configuration binder does not replace a list
    /// that already has items, it appends the configured ones to them (every default would be listed
    /// twice, and a configured list could never drop a default).
    /// </summary>
    public static T[] ConfiguredOrDefault<T>(T[]? configured, T[] defaults) =>
        configured is { Length: > 0 } ? configured : defaults;
}
