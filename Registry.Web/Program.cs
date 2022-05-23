using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommandLine;
using Registry.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Adapters.DroneDB;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Serilog;

namespace Registry.Web
{
    public class Program
    {
        public const int DefaultPort = 5000;
        public const string DefaultHost = "localhost";
        public const string SpaRoot = "ClientApp";
        public const string AppSettingsFileName = "appsettings.json";
        private const string AppSettingsDefaultFileName = "appsettings-default.json";

        public static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args)
                .WithParsed(RunOptions);
        }

        private static void RunOptions(Options opts)
        {
            var appVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            var appName = AppDomain.CurrentDomain.FriendlyName;

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\t\t*** {appName} - v{appVersion} ***");
            Console.ResetColor();
            Console.WriteLine();

            if (!VerifyOptions(opts)) return;

            Directory.CreateDirectory(opts.StorageFolder);
            Environment.CurrentDirectory = opts.StorageFolder;

            Console.WriteLine(" ?> Using storage folder '{0}'", opts.StorageFolder);
            Console.WriteLine(" ?> Using address '{0}'", opts.Address);

            try
            {
                Console.WriteLine(" ?> Using DDB version " + DDBWrapper.GetVersion());
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" !> Error while invoking DDB bindings. Did you make sure to place the DDB DLLs? " +
                                  e.Message);
                Console.ResetColor();
                return;
            }

            if (!SetupStorageFolder(opts.StorageFolder, opts.ResetHub))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" !> Error while setting up storage folder");
                Console.ResetColor();
                return;
            }

            try
            {
                Log.Information("Starting web host");
                Console.WriteLine(" -> Starting web host");

                var args = new[] { "--urls", $"http://{opts.Address}" };

                Host.CreateDefaultBuilder(args)
                    .UseSerilog((context, services, configuration) => configuration
                        .ReadFrom.Configuration(context.Configuration)
                        .ReadFrom.Services(services)
                        .Enrich.FromLogContext())
                    .ConfigureWebHostDefaults(webBuilder => webBuilder.UseStartup<Startup>())
                    .Build()
                    .Run();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Host terminated unexpectedly");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private static bool VerifyOptions(Options opts)
        {
            if (opts.StorageFolder == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" !> Storage folder not specified");
                Console.ResetColor();
                return false;
            }

            if (opts.Address != null)
            {
                if (opts.Address.Length == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(" !> Address not specified");
                    Console.ResetColor();
                    return false;
                }

                var match = Regex.Match(opts.Address, @"(?<host>[a-z\.]+)?:?(?<port>\d+)?", RegexOptions.IgnoreCase);

                if (!match.Success)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(" !> Address not valid");
                    Console.ResetColor();
                    return false;
                }

                var host = match.Groups["host"].Success ? match.Groups["host"].Value : DefaultHost;
                var port = match.Groups["port"].Success ? int.Parse(match.Groups["port"].Value) : DefaultPort;

                // Check valid port
                if (port is < 1 or > 65535)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(" !> Address not valid (port out of range");
                    Console.ResetColor();
                    return false;
                }

                opts.Address = $"{host}:{port}";
            }

            return true;
        }

        private static bool SetupStorageFolder(string folder, bool resetSpa = false)
        {
            Console.WriteLine(" -> Setting up storage folder");

            Directory.CreateDirectory(folder);

            SetupHub(folder, resetSpa);

            var settingsFilePath = Path.Combine(folder, AppSettingsFileName);

            var defaultSettingsConfig = GetDefaultSettings();
            var defaultSettings = defaultSettingsConfig?["AppSettings"]!.ToObject<AppSettings>();

            // The appsettings file does not contain only appsettings
            if (!File.Exists(settingsFilePath))
            {
                Console.WriteLine(" -> Creating default appsettings.json");
                File.WriteAllText(AppSettingsFileName,
                    JsonConvert.SerializeObject(defaultSettingsConfig, Formatting.Indented));
            }

            Console.WriteLine(" -> Verifying settings");
            try
            {
                var settingsConfig = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(settingsFilePath));
                var settings = settingsConfig?["AppSettings"]?.ToObject<AppSettings>();

                if (!VerifySettings(settings, defaultSettings))
                {
                    Console.WriteLine(" !> Error while verifying settings");
                    return false;
                }

                // Update settings
                Console.WriteLine(" -> Updating settings");
                settingsConfig["AppSettings"] = JObject.FromObject(settings);
                File.WriteAllText(settingsFilePath, JsonConvert.SerializeObject(settingsConfig, Formatting.Indented));
            }
            catch (Exception e)
            {
                Console.WriteLine(" !> Error while verifying settings");
                Console.WriteLine(e.Message);
                return false;
            }


            return true;
        }

        private static void SetupHub(string folder, bool resetSpa)
        {
            var hubRoot = Path.Combine(folder, SpaRoot);

            if (resetSpa)
            {
                Console.WriteLine(" -> Resetting Hub");
                Directory.Delete(hubRoot, true);
            }
            else if (Directory.Exists(hubRoot)) return;

            ExtractHub(hubRoot);
        }

        private static void ExtractHub(string folder)
        {
            var efp = new EmbeddedResourceQuery();
            var executingAssembly = Assembly.GetExecutingAssembly();

            Directory.CreateDirectory(folder);

            Console.WriteLine(" -> Extracting Hub");

            // Read embedded resource and extract to storage folder
            using var stream = efp.Read(executingAssembly, SpaRoot + ".zip");
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            zip.ExtractToDirectory(folder);
        }

        private static JObject GetDefaultSettings()
        {
            var efp = new EmbeddedResourceQuery();
            var executingAssembly = Assembly.GetExecutingAssembly();
            using var reader = new StreamReader(efp.Read(executingAssembly, AppSettingsDefaultFileName));
            return JsonConvert.DeserializeObject<JObject>(reader.ReadToEnd());
        }

        private static bool VerifySettings(AppSettings settings, AppSettings defaultSettings)
        {
            if (settings == null)
            {
                Console.WriteLine(" !> Empty settings, check appsettings.json");
                return false;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(settings.Secret))
                    settings.Secret = CommonUtils.RandomString(64);

                var externalUrl = settings.ExternalUrlOverride;

                if (!string.IsNullOrWhiteSpace(externalUrl) && !Uri.TryCreate(externalUrl, UriKind.Absolute, out _))
                {
                    Console.WriteLine(" !> ExternalUrlOverride is not a valid URL");
                    return false;
                }

                if (settings.TokenExpirationInDays < 1)
                {
                    Console.WriteLine(" !> TokenExpirationInDays is not valid (must be greater than 0)");
                    return false;
                }

                var defaultAdmin = settings.DefaultAdmin;

                if (defaultAdmin?.Email == null || defaultAdmin.Password == null || defaultAdmin.UserName == null)
                {
                    Console.WriteLine(" !> DefaultAdmin is not valid");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(settings.DatasetsPath))
                {
                    Console.WriteLine(" !> DatasetsPath is empty");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(settings.CachePath))
                {
                    Console.WriteLine(" !> CachePath is empty");
                    return false;
                }

                if (settings.BatchTokenLength < 8)
                {
                    Console.WriteLine(" !> BatchTokenLength is not valid (must be at least 8)");
                    return false;
                }

                if (settings.UploadBatchTimeout.TotalMinutes < 1)
                {
                    Console.WriteLine(" !> UploadBatchTimeout is not valid (must be at least 1 minute)");
                    return false;
                }

                if (settings.RandomDatasetNameLength < 8)
                {
                    Console.WriteLine(" !> RandomDatasetNameLength is not valid (must be at least 8)");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(settings.AuthCookieName))
                {
                    Console.WriteLine(" !> AuthCookieName is empty");
                    return false;
                }

                if (settings.ThumbnailsCacheExpiration.HasValue &&
                    settings.ThumbnailsCacheExpiration.Value.TotalMinutes < 1)
                {
                    Console.WriteLine(" !> ThumbnailsCacheExpiration is not valid (must be at least 1 minute)");
                    return false;
                }

                if (settings.TilesCacheExpiration.HasValue && settings.TilesCacheExpiration.Value.TotalMinutes < 1)
                {
                    Console.WriteLine(" !> TilesCacheExpiration is not valid (must be at least 1 minute)");
                    return false;
                }

                if (settings.ClearCacheInterval.HasValue && settings.ClearCacheInterval.Value.TotalMinutes < 1)
                {
                    Console.WriteLine(" !> ClearCacheInterval is not valid (must be at least 1 minute)");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(" !> Exception: " + ex.Message);
                return false;
            }

            return true;
        }
    }
}