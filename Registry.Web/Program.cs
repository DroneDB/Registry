using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Registry.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Registry.Web
{
    public class Program
    {

        const string ConfigFilePath = "appsettings.json";
        
        public static void Main(string[] args)
        {

            if (!CheckConfig())
            {
                Console.WriteLine(" ?> Errors occurred during config validation. Check console to find out what's wrong.");
                return;
            }

            CreateHostBuilder(args).Build().Run();
        }

        private static bool CheckConfig()
        {

            if (!File.Exists(ConfigFilePath))
            {
                Console.WriteLine($" !> Cannot find {ConfigFilePath}");
                Console.WriteLine(" -> Generating default config");

                File.WriteAllText(ConfigFilePath, DefaultConfig);
                
            }

            var jObject = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(ConfigFilePath));

            var appSettings = jObject["AppSettings"];

            if (appSettings == null)
            {
                Console.WriteLine(" !> Cannot find AppSettings section in config file");
                return false;
            }

            var secret = appSettings["Secret"];

            // If secret does not exist
            if (secret == null || string.IsNullOrWhiteSpace(secret.Value<string>()))
            {
                Console.WriteLine(" !> Secret not found in config");

                var str = Utils.RandomString(64);

                appSettings["Secret"] = str;

                Console.WriteLine($" -> Generated secret: '{str}'");

                File.WriteAllText(ConfigFilePath, JsonConvert.SerializeObject(jObject, Formatting.Indented));
                
            }

            return true;

        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });

        private const string DefaultConfig = @"{
  ""AppSettings"": {
    ""Secret"": """",
    ""TokenExpirationInDays"": 7 
  },
  ""Logging"": {
    ""LogLevel"": {
      ""Default"": ""Information"",
      ""Microsoft"": ""Warning"",
      ""Microsoft.Hosting.Lifetime"": ""Information""
    }
  },
  ""AllowedHosts"": ""*"",
  ""ConnectionStrings"": {
  }
}";

    }
}
