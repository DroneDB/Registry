using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Builds the ConfigurationDataDto from AppSettings + embedded defaults.
/// Named with "Data" suffix to avoid ambiguity with Microsoft.Extensions.Configuration.IConfigurationBuilder.
/// </summary>
public interface IConfigurationDataBuilder
{
    /// <summary>
    /// Builds the complete configuration data DTO for the admin config editor page.
    /// </summary>
    ConfigurationDataDto Build();
}
