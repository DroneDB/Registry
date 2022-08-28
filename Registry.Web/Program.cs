using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Adapters.DroneDB;
using Registry.Web.Data;
using Registry.Web.Identity;
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
        public const string DroneDBDllName = "ddb.dll";

        public static readonly Version MinDdbVersion = new(1, 0, 7);

        public static void Main(string[] args)
        {
#if DEBUG_EF
            // EF core tools compatibility
            if (IsEfTool(args))
            {
                RunEfToolsHost(args);
                return;
            }
#endif

            Parser.Default.ParseArguments<Options>(args)
                .WithParsed(RunOptions);
        }

        private static bool FixPath()
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (path == null)
            {
                Console.WriteLine(" !> PATH is not set");
                return false;
            }

            var newPath = path.Split(Path.PathSeparator);

            var ddbFolder = newPath.FirstOrDefault(
                p => File.Exists(Path.Combine(p, DroneDBDllName)));
            
            if (ddbFolder == null)
            {
                Console.WriteLine(" !> {0} not found in PATH", DroneDBDllName);
                return false;
            }

            Console.WriteLine(" ?> Found {0} in {1}", DroneDBDllName, ddbFolder);

            Environment.SetEnvironmentVariable("PATH",
                $"{ddbFolder}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");

            return true;
        }

        private static void RunOptions(Options opts)
        {
            var appVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            var appName = AppDomain.CurrentDomain.FriendlyName;

            CommonUtils.WriteLineColor($"\n\t\t*** {appName} - v{appVersion} ***\n", ConsoleColor.Green);

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                CommonUtils.WriteLineColor("\n -> Exiting...\n", ConsoleColor.Yellow);
            };

            // If we need to initialize ddb from the storage folder
            var lateDdbInit = false;
            
            // Ensure the ddb.dll is in the PATH and fix it if needed (only on windows)
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                lateDdbInit = !FixPath();

#if DEBUG
            Console.WriteLine(" ?> Running in DEBUG");
#else
            Console.WriteLine(" ?> Running in RELEASE");
#endif

            if (!VerifyOptions(opts)) return;

            opts.StorageFolder = Path.GetFullPath(opts.StorageFolder);
            Directory.CreateDirectory(opts.StorageFolder);
            Environment.CurrentDirectory = opts.StorageFolder;

            Console.WriteLine(" ?> Using storage folder '{0}'", opts.StorageFolder);
            Console.WriteLine(" ?> Using address '{0}'", opts.Address);

            if (lateDdbInit)
            {
                if (!SetupStorageFolder(opts.StorageFolder, opts.ResetHub, true))
                {
                    CommonUtils.WriteLineColor(" !> Failed to setup storage folder", ConsoleColor.Red);
                    return;
                }
            
                if (!InitializeDdb())
                {
                    CommonUtils.WriteLineColor(" !> Failed to initialize DroneDB", ConsoleColor.Red);
                    return;
                }
            }
            else
            {
                if (!InitializeDdb())
                {
                    CommonUtils.WriteLineColor(" !> Failed to initialize DroneDB", ConsoleColor.Red);
                    return;
                }
                
                if (!SetupStorageFolder(opts.StorageFolder, opts.ResetHub))
                {
                    CommonUtils.WriteLineColor(" !> Failed to setup storage folder", ConsoleColor.Red);
                    return;
                }
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

        private static bool InitializeDdb()
        {
            try
            {
#if DEBUG
                DDBWrapper.RegisterProcess(true);
                Console.WriteLine(" ?> Initialized DDB - Verbose");
#else
                DDBWrapper.RegisterProcess(false);
                Console.WriteLine(" ?> Initialized DDB");
#endif

                var rawVersion = DDBWrapper.GetVersion();
                Console.WriteLine(" ?> DDB version " + rawVersion);

                // Remove git commit string
                rawVersion = rawVersion.Contains(' ') ? rawVersion[..rawVersion.IndexOf(' ')] : rawVersion;

                var ddbVersion = new Version(rawVersion);

                if (ddbVersion < MinDdbVersion)
                {
                    CommonUtils.WriteLineColor(
                        $" !> DDB version is too old, please upgrade to {MinDdbVersion} or higher: {MagicStrings.DdbReleasesPageUrl}",
                        ConsoleColor.Red);
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($" !> Unable to load DDB lib: {e.Message}");
                Console.ResetColor();
                Console.WriteLine();

                Console.WriteLine(
                    $" ?> Check installation instructions on {MagicStrings.DdbInstallPageUrl} and try again.");
                Console.WriteLine($" ?> Releases page: {MagicStrings.DdbReleasesPageUrl}");

                return false;
            }

        }

        private static bool VerifyOptions(Options opts)
        {
            if (opts.StorageFolder == null)
            {
                CommonUtils.WriteColor(" !> Storage folder is not specified", ConsoleColor.Red);
                return false;
            }

            if (opts.Address != null)
            {
                if (opts.Address.Length == 0)
                {
                    CommonUtils.WriteColor(" !> Address not specified", ConsoleColor.Red);
                    return false;
                }

                var parts = opts.Address.Split(':');

                var host = DefaultHost;
                var port = DefaultPort;

                switch (parts.Length)
                {
                    case 1:
                    {
                        if (!int.TryParse(parts[0], out port))
                        {
                            // Try to parse as hostname
                            host = parts[0];

                            if (host.ToLowerInvariant() != "localhost" && !IPEndPoint.TryParse(host, out _))
                            {
                                CommonUtils.WriteColor(" !> Invalid address", ConsoleColor.Red);
                                return false;
                            }
                        }

                        if (port is < 1 or > 65535)
                        {
                            CommonUtils.WriteColor(" !> Invalid port", ConsoleColor.Red);
                            return false;
                        }

                        break;
                    }
                    case 2:
                    {
                        host = parts[0];

                        if (host.ToLowerInvariant() != "localhost" && !IPEndPoint.TryParse(host, out _))
                        {
                            CommonUtils.WriteColor(" !> Invalid address", ConsoleColor.Red);
                            return false;
                        }

                        if (!int.TryParse(parts[1], out port))
                        {
                            CommonUtils.WriteColor(" !> Invalid port", ConsoleColor.Red);
                            return false;
                        }

                        if (port is < 1 or > 65535)
                        {
                            CommonUtils.WriteColor(" !> Invalid port", ConsoleColor.Red);
                            return false;
                        }

                        break;
                    }
                    default:
                        CommonUtils.WriteColor(" !> Invalid address", ConsoleColor.Red);
                        return false;
                }

                opts.Address = $"{host}:{port}";
            }

            return true;
        }

        private static bool SetupStorageFolder(string folder, bool resetSpa = false, bool deployDdb = false)
        {
            Console.WriteLine(" -> Setting up storage folder");

            Directory.CreateDirectory(folder);

            if (deployDdb && !SetupDdb(folder, false))
            {
                CommonUtils.WriteLineColor(" !> Failed to deploy DDB to storage folder", ConsoleColor.Red);
                return false;
            }
                

            if (!SetupHub(folder, resetSpa))
            {
                Console.WriteLine(" !> Error while setting up Hub");
                return false;
            }

            var settingsFilePath = Path.Combine(folder, MagicStrings.AppSettingsFileName);

            var defaultSettingsConfig = GetDefaultSettings();
            var defaultSettings = defaultSettingsConfig?["AppSettings"]!.ToObject<AppSettings>();

            // The appsettings file does not contain only appsettings
            if (!File.Exists(settingsFilePath))
            {
                Console.WriteLine(" -> Creating default appsettings.json");

                File.WriteAllText(MagicStrings.AppSettingsFileName,
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

        private static bool SetupHub(string folder, bool resetSpa)
        {
            var hubRoot = Path.Combine(folder, MagicStrings.SpaRoot);

            if (resetSpa)
            {
                Console.WriteLine(" -> Resetting Hub");
                Directory.Delete(hubRoot, true);
            }
            else if (
                Directory.Exists(hubRoot) &&
                Directory.GetFiles(hubRoot).Any())
            {
                Console.WriteLine(" ?> Hub folder is ok");
                return true;
            }

            return ExtractHub(hubRoot);
        }
        
        private static bool SetupDdb(string folder, bool resetDdb)
        {
            var ddbRoot = Path.Combine(folder, MagicStrings.DdbArchive);

            if (resetDdb)
            {
                Console.WriteLine(" -> Resetting ddb installation");
                Directory.Delete(ddbRoot, true);
            }
            else if (
                Directory.Exists(ddbRoot) &&
                Directory.GetFiles(ddbRoot).Any())
            {
                Console.WriteLine(" ?> Ddb folder is ok");
                
                Environment.SetEnvironmentVariable("PATH",
                    $"{ddbRoot}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");
                
                return true;
            }

            var res = ExtractDdb(ddbRoot);

            if (!res) return false;

            Environment.SetEnvironmentVariable("PATH",
                $"{ddbRoot}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");

            return true;
        }

        private static bool ExtractEmbeddedArchive(string archiveName, string folder)
        {
            var efp = new EmbeddedResourceQuery();
            var executingAssembly = Assembly.GetExecutingAssembly();

            Directory.CreateDirectory(folder);

            Console.WriteLine($" -> Extracting '{archiveName}'");

            // Read embedded resource and extract to storage folder
            using var stream = efp.Read(executingAssembly, archiveName + ".zip");

            if (stream == null)
            {
                Console.WriteLine($" !> Failed to read embedded archive");
                return false;
            }
            
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            if (!zip.Entries.Any())
            {
                Console.WriteLine($" !> Error while extracting '{archiveName}', empty archive");
                return false;
            }

            Console.WriteLine(" ?> Found {0} entries", zip.Entries.Count);

            zip.ExtractToDirectory(folder);

            return true;
        }

        private static bool ExtractDdb(string folder)
        {
            return ExtractEmbeddedArchive(MagicStrings.DdbArchive, folder);
        }
        
        private static bool ExtractHub(string folder)
        {
            return ExtractEmbeddedArchive(MagicStrings.SpaRoot, folder);
        }

        private static JObject GetDefaultSettings()
        {
            var efp = new EmbeddedResourceQuery();
            var executingAssembly = Assembly.GetExecutingAssembly();
            using var reader = new StreamReader(efp.Read(executingAssembly, MagicStrings.AppSettingsDefaultFileName));
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

        #region EF Tool

        /*
            Add new migration (RegistryContext)
            dotnet ef migrations add NewMigration --project Registry.Web.Data.SqliteMigrations --context Registry.Web.Data.RegistryContext --configuration DebugEF --startup-project Registry.Web --verbose -- --provider Sqlite
            dotnet ef migrations add NewMigration --project Registry.Web.Data.MysqlMigrations  --context Registry.Web.Data.RegistryContext --configuration DebugEF --startup-project Registry.Web --verbose -- --provider MySql

            Generate SQL script (RegistryContext)
            dotnet ef migrations script --project Registry.Web.Data.SqliteMigrations --context Registry.Web.Data.RegistryContext --configuration DebugEF --startup-project Registry.Web -o sqlite.sql --verbose -- --provider Sqlite
            dotnet ef migrations script --project Registry.Web.Data.MysqlMigrations  --context Registry.Web.Data.RegistryContext --configuration DebugEF --startup-project Registry.Web -o mysql.sql --verbose -- --provider MySql

            Add new migration (ApplicationDbContext)
            dotnet ef migrations add NewMigration --project Registry.Web.Identity.SqliteMigrations --context Registry.Web.Identity.ApplicationDbContext --configuration DebugEF --startup-project Registry.Web --verbose -- --provider Sqlite
            dotnet ef migrations add NewMigration --project Registry.Web.Identity.MysqlMigrations  --context Registry.Web.Identity.ApplicationDbContext --configuration DebugEF --startup-project Registry.Web --verbose -- --provider MySql

            Generate SQL script (ApplicationDbContext)
            dotnet ef migrations script --project Registry.Web.Identity.SqliteMigrations --context Registry.Web.Identity.ApplicationDbContext --configuration DebugEF --startup-project Registry.Web -o sqlite.sql --verbose -- --provider Sqlite
            dotnet ef migrations script --project Registry.Web.Identity.MysqlMigrations  --context Registry.Web.Identity.ApplicationDbContext --configuration DebugEF --startup-project Registry.Web -o mysql.sql --verbose -- --provider MySql

        */

#if DEBUG_EF
        private static bool IsEfTool(string[] args)
        {
            return args.Length == 4 && args[0] == "--provider" && args[2] == "--applicationName";
        }
        
        private static void RunEfToolsHost(string[] args)
        {
            Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration(x => x.AddJsonFile("appsettings-eftools.json"))
                .ConfigureServices(
                    (hostContext, services) =>
                    {
                        // Set the active provider via configuration
                        var configuration = hostContext.Configuration;
                        var provider = configuration.GetValue("Provider", "Sqlite");

                        var registrySqliteConnectionString =
                            configuration.GetConnectionString("RegistrySqliteConnection");
                        var registryMysqlConnectionString =
                            configuration.GetConnectionString("RegistryMysqlConnection");
                        
                        /*var registrySqlServerConnectionString =
                            configuration.GetConnectionString("RegistrySqlServerConnection");*/

                        services.AddDbContext<RegistryContext>(
                            options => _ = provider switch
                            {
                                "Sqlite" => options.UseSqlite(
                                    registrySqliteConnectionString,
                                    x => x.MigrationsAssembly("Registry.Web.Data.SqliteMigrations")),

                                "MySql" => options.UseMySql(
                                    registryMysqlConnectionString, ServerVersion.AutoDetect(registryMysqlConnectionString),
                                    x => x.MigrationsAssembly("Registry.Web.Data.MySqlMigrations")),

                                /* NOTE: This is not implemented yet
                                "SqlServer" => options.UseSqlServer(registrySqlServerConnectionString, 
                                    x => x.MigrationsAssembly("Registry.Web.Data.SqlServerMigrations")),
                                    */
                                
                                _ => throw new Exception($"Unsupported provider: {provider}")
                            });
                        
                        var identitySqliteConnectionString =
                            configuration.GetConnectionString("IdentitySqliteConnection");
                        var identityMysqlConnectionString =
                            configuration.GetConnectionString("IdentityMysqlConnection");
                        
                        /*var identitySqlServerConnectionString =
                            configuration.GetConnectionString("IdentitySqlServerConnection");*/

                        services.AddDbContext<ApplicationDbContext>(
                            options => _ = provider switch
                            {
                                "Sqlite" => options.UseSqlite(
                                    identitySqliteConnectionString,
                                    x => x.MigrationsAssembly("Registry.Web.Identity.SqliteMigrations")),

                                "MySql" => options.UseMySql(
                                    identityMysqlConnectionString, ServerVersion.AutoDetect(identityMysqlConnectionString),
                                    x => x.MigrationsAssembly("Registry.Web.Identity.MySqlMigrations")),

                                /* NOTE: This is not implemented yet
                                "SqlServer" => options.UseSqlServer(identitySqlServerConnectionString, 
                                    x => x.MigrationsAssembly("Registry.Web.Identity.SqlServerMigrations")),
                                    */
                                
                                _ => throw new Exception($"Unsupported provider: {provider}")
                            });
                    })
                .Build()
                .Run();
        }
#endif

        #endregion
    }
}