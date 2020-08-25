using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Registry.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;

namespace Registry.Web
{
    public class Program
    {

        const string ConfigFilePath = "appsettings.json";
        const string DefaultConfigFilePath = "appsettings-default.json";

        public static readonly PackageVersion SupportedDdbVersion = new PackageVersion(0,9,2);

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
                Console.WriteLine(" -> Copying default config");

                File.Copy(DefaultConfigFilePath, ConfigFilePath, true);
            }

            var defaultConfig = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(DefaultConfigFilePath));
            var config = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(ConfigFilePath));

            var defaultAppSettings = defaultConfig["AppSettings"];
            var appSettings = config["AppSettings"];

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

            }

            // TODO: Check
            var ddbPath = appSettings["DdbPath"];

            if (ddbPath == null || string.IsNullOrWhiteSpace(ddbPath.Value<string>()))
            {
                Console.WriteLine(" !> Ddb path not found in config");
                appSettings["DdbPath"] = defaultAppSettings["DdbPath"];
                ddbPath = defaultAppSettings["DdbPath"];
                Console.WriteLine($" -> Copied from default config");
            }

            var ddbPathVal = ddbPath.Value<string>();

            IDdbPackageProvider ddbPackageProvider = new DdbPackageProvider(ddbPathVal, appSettings["SupportedDdbVersion"]?.ToObject<PackageVersion>() ?? SupportedDdbVersion);

            // TODO: Add config parameter to ignore ddb version match

            if (!ddbPackageProvider.IsDdbReady()) 
            {
                Console.WriteLine($" !> Ddb not ready in path '{ddbPathVal}' ");
                Console.WriteLine(" ?> Follow these instruction to install it: https://github.com/DroneDB/Registry/wiki");
                return false;
            }

            var connectionStrings = config["ConnectionStrings"];

            if (connectionStrings == null)
            {
                Console.WriteLine(" !> Cannot find ConnectionStrings section in config file");
                return false;
            }

            var defaultConnectionStrings = defaultConfig["ConnectionStrings"];

            if (!CheckConnection(connectionStrings, defaultConnectionStrings, "IdentityConnection")) return false;
            if (!CheckConnection(connectionStrings, defaultConnectionStrings, "RegistryConnection")) return false;

            var defaultAdmin = appSettings["DefaultAdmin"];

            if (defaultAdmin == null)
            {
                Console.WriteLine(" !> Cannot find default admin info, copying from default config");

                if (defaultAppSettings == null)
                {
                    Console.WriteLine(" !> Cannot find default admin in default config");
                    return false;
                }

                appSettings["DefaultAdmin"] = defaultAppSettings["DefaultAdmin"];

            }

            // Update config
            File.WriteAllText(ConfigFilePath, JsonConvert.SerializeObject(config, Formatting.Indented));

            return true;

        }

        private static bool CheckConnection(JToken connectionStrings, JToken defaultConnectionStrings, string connectionName)
        {
            var connection = connectionStrings[connectionName];

            if (connection == null || string.IsNullOrWhiteSpace(connection.Value<string>()))
            {
                Console.WriteLine(" !> Cannot find " + connectionName);

                if (defaultConnectionStrings == null)
                {
                    Console.WriteLine(" !> Cannot find connection strings in default config");
                    return false;
                }

                var defaultConnection = defaultConnectionStrings[connectionName];
                if (defaultConnection == null || string.IsNullOrWhiteSpace(defaultConnection.Value<string>()))
                {
                    Console.WriteLine(" !> Cannot copy " + connectionName + " from default config");
                    return false;
                }

                connectionStrings[connectionName] = defaultConnectionStrings[connectionName];

                Console.WriteLine(" -> Setting " + connectionName + " to default");
                Console.WriteLine($" ?> {connectionStrings[connectionName]}");
            }

            return true;
        }

        private static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });


    }
}
