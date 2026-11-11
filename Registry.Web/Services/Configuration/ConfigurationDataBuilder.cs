using System.Collections.Generic;
using System.Linq;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Configuration;

/// <summary>
/// The admin configuration view (<c>GET /sys/config</c>): the sections of every <see cref="IConfigurationSectionBuilder"/>
/// in page order (the 1180-line builder over the whole <c>AppSettings</c> became one builder per settings
/// area over <see cref="ConfigurationView"/>).
/// </summary>
public sealed class ConfigurationDataBuilder
{
    private readonly IReadOnlyList<IConfigurationSectionBuilder> _builders;

    public ConfigurationDataBuilder(IEnumerable<IConfigurationSectionBuilder> builders) => _builders = [.. builders];

    /// <summary>
    /// Builds the complete configuration data DTO for the admin config editor page.
    /// </summary>
    public ConfigurationDataDto Build() => new()
    {
        Sections = [.. _builders.SelectMany(b => b.Build()).OrderBy(s => s.Place).Select(s => s.Section)]
    };
}
