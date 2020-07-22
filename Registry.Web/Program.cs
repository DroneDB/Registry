using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
            // We could use a library to perform command line parsing, but this is sufficient so far
            if (args.Any(a => 
                    string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase)))
            {
                ShowHelp();
                return;
            }

            if (!CheckConfig())
            {
                Console.WriteLine(" ?> Errors occurred during config validation. Check console to find out what's wrong.");
                return;
            }

            CreateHostBuilder(args).Build().Run();
        }

        private static void ShowHelp()
        {
            var appVersion = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyFileVersionAttribute>()
                ?.Version;
            var appName = AppDomain.CurrentDomain.FriendlyName;

            Console.WriteLine($"{appName} - v{appVersion}");

            Console.WriteLine("Hosts the API of the DroneDB Registry");
            Console.WriteLine();
            Console.WriteLine($"Usage: {appName} [flags]");
            Console.WriteLine();
            Console.WriteLine("Flags:");
            Console.WriteLine("\t--urls\t\"https://host:https_port;http://host:http_port\"");
            Console.WriteLine("\t\tAddresses to bind to. Defaults to \"http://localhost:5000;https://localhost:5001\"");
            Console.WriteLine();

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

                var str = CommonUtils.RandomString(64);

                appSettings["Secret"] = str;

                Console.WriteLine($" -> Generated secret: '{str}'");

                File.WriteAllText(ConfigFilePath, JsonConvert.SerializeObject(jObject, Formatting.Indented));

            }

            var connectionStrings = jObject["ConnectionStrings"];

            if (connectionStrings == null)
            {
                Console.WriteLine(" !> Cannot find ConnectionStrings section in config file");
                return false;
            }

            var identityConnection = connectionStrings["IdentityConnection"];

            if (identityConnection == null || string.IsNullOrWhiteSpace(identityConnection.Value<string>()))
            {
                Console.WriteLine(" !> Cannot find IdentityConnection");

                connectionStrings["IdentityConnection"] = DefaultSqliteConnectionString;

                Console.WriteLine(" -> Setting IdentityConnection to Sqlite default");
                Console.WriteLine($" ?> {DefaultSqliteConnectionString}");

                File.WriteAllText(ConfigFilePath, JsonConvert.SerializeObject(jObject, Formatting.Indented));

            }


            return true;

        }

        private static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });


        private const string DefaultSqliteConnectionString = "Data Source=App_Data/identity.db;Mode=ReadWriteCreate";

        private static readonly string DefaultConfig = $@"{{
  ""AppSettings"": {{
    ""Secret"": """",
    ""TokenExpirationInDays"": 7,
    ""AuthProvider"": ""Sqlite""
  }},
  ""Logging"": {{
    ""LogLevel"": {{
      ""Default"": ""Information"",
      ""Microsoft"": ""Warning"",
      ""Microsoft.Hosting.Lifetime"": ""Information""
    }}
  }},
  ""AllowedHosts"": ""*"",
  ""ConnectionStrings"": {{
        ""IdentityConnection"": ""{DefaultSqliteConnectionString}""
  }}
}}";

    }
}
