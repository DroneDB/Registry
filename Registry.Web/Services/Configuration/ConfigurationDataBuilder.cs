#nullable enable
using System.Collections.Generic;
using System.Linq;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Configuration;

/// <summary>
/// The admin configuration view (<c>GET /sys/config</c>): the sections of every <see cref="IConfigurationSectionBuilder"/>
/// in page order (RQ-12: the 1180-line builder over the whole <c>AppSettings</c> became one builder per settings
/// area over <see cref="IConfigurationView"/>).
/// </summary>
public sealed class ConfigurationDataBuilder : IConfigurationDataBuilder
{
    private readonly IReadOnlyList<IConfigurationSectionBuilder> _builders;

    public ConfigurationDataBuilder(IEnumerable<IConfigurationSectionBuilder> builders) => _builders = [.. builders];

    public ConfigurationDataDto Build() => new()
    {
        Sections = [.. _builders.SelectMany(b => b.Build()).OrderBy(s => s.Place).Select(s => s.Section)]
    };
}
