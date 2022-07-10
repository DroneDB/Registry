using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

public class ConfigurationHelper : IConfigurationHelper<AppSettings>
{
    private readonly string _configPath;

    public ConfigurationHelper(string configPath)
    {
        _configPath = configPath;
    }
    
    public AppSettings GetConfiguration()
    {
        // The appsettings file does not contain only appsettings
        if (!File.Exists(_configPath))
        {
            return null;
        }

        var settingsConfig = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(_configPath));
        var settings = settingsConfig?["AppSettings"]?.ToObject<AppSettings>();

        return settings;
    }

    public void SaveConfiguration(AppSettings configuration)
    {
        var settingsConfig = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(_configPath));
        settingsConfig["AppSettings"] = JObject.FromObject(configuration);

        File.WriteAllText(_configPath, JsonConvert.SerializeObject(settingsConfig, Formatting.Indented));
    }
}