#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Registry.Web.Hosting;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Configuration;

/// <summary>
/// The settings the admin configuration view compares, per focused settings class of RQ-07 (the same section map
/// as the live options, <see cref="RegistryOptions.SectionOf{T}"/>): what is configured, and the default shipped in
/// <c>appsettings-default.json</c>.
/// </summary>
public interface IConfigurationView
{
    /// <summary>
    /// <typeparamref name="T"/> as configured: bound from the configuration like the live options, without the
    /// defaults they apply afterwards (the view shows what the settings file holds, the editor writes it back).
    /// </summary>
    T Configured<T>() where T : class, new();

    /// <summary><see cref="Configured{T}"/>, or null when the configuration has no section for it.</summary>
    T? ConfiguredOrNull<T>() where T : class, new();

    /// <summary><typeparamref name="T"/> as <c>appsettings-default.json</c> ships it.</summary>
    T Shipped<T>() where T : class, new();

    /// <summary><see cref="Shipped{T}"/>, or null when the defaults have no section for it.</summary>
    T? ShippedOrNull<T>() where T : class, new();
}

/// <summary>Default <see cref="IConfigurationView"/>; the defaults are read once.</summary>
public sealed class ConfigurationView : IConfigurationView
{
    private readonly IConfiguration _configuration;
    private readonly Lazy<JObject> _defaults = new(() => JObject.Parse(SettingsFile.DefaultContent()));

    public ConfigurationView(IConfiguration configuration) => _configuration = configuration;

    public T Configured<T>() where T : class, new() => ConfiguredOrNull<T>() ?? new T();

    public T? ConfiguredOrNull<T>() where T : class, new()
    {
        var section = _configuration.GetSection(RegistryOptions.SectionOf<T>());
        return section.Exists() ? section.Get<T>() ?? new T() : null;
    }

    public T Shipped<T>() where T : class, new() => ShippedOrNull<T>() ?? new T();

    public T? ShippedOrNull<T>() where T : class, new() =>
        RegistryOptions.SectionOf<T>().Split(':')
            .Aggregate((JToken?)_defaults.Value, (token, key) => token?[key])?.ToObject<T>();
}

/// <summary>Where a section goes in the admin configuration view: the order of the members is the page order.</summary>
public enum ConfigurationSection
{
    Auth,
    DefaultAdmin,
    Providers,
    StoragePaths,
    Upload,
    Downloads,
    Cache,
    Redis,
    CronJobs,
    PasswordPolicy,
    HubOptions,
    OrgManagement,
    Thumbnails,
    ExportZip,
    Ldap,
    ProcessingPlatform,
    ToolGating,
    ProcessingBudget,
    HostBudget,
    Import,
    ConnectionStrings,
    Serilog,
    Misc
}

/// <summary>A section of the admin configuration view and its place.</summary>
public sealed record PlacedSection(ConfigurationSection Place, ConfigurationSectionDataDto Section);

/// <summary>The sections of the admin configuration view for one area of the settings.</summary>
public interface IConfigurationSectionBuilder
{
    IEnumerable<PlacedSection> Build();
}
