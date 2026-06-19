using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Generic configuration read/write interface.
/// </summary>
public interface IConfigurationHelper<T>
{
    public T GetConfiguration();
    public void SaveConfiguration(T configuration);
}