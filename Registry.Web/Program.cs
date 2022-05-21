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
                Console.WriteLine(" !> Error while invoking DDB bindings. Did you make sure to place the DDB DLLs? " + e.Message);
                Console.ResetColor();
                return;
            }
            
            SetupStorageFolder(opts.StorageFolder);
            
            try
            {
                Log.Information("Starting web host");

                var args = new[] { "--urls", $"http://{opts.Address}" };
                
                Host.CreateDefaultBuilder(args)
                    /*.ConfigureHostConfiguration(config =>
                    {
                        config.AddJsonFile(Path.Combine(opts.StorageFolder, "appsettings.json"), optional: false, reloadOnChange: true);
                    })*/
                    .UseSerilog((context, services, configuration) => configuration
                        .ReadFrom.Configuration(context.Configuration)
                        .ReadFrom.Services(services)
                        .Enrich.FromLogContext())
                    .ConfigureWebHostDefaults(webBuilder => webBuilder.UseStartup<Startup>())
                    .Build()
                    .Run();
                
                //CreateHostBuilder(args).Build().Run();
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

        private static void SetupStorageFolder(string folder)
        {
            var spaRoot = Path.Combine(folder, SpaRoot);

            var efp = new EmbeddedResourceQuery();

            var executingAssembly = Assembly.GetExecutingAssembly();
            
            var resources = efp.GetResourceNames(executingAssembly);

            var spaResources = from res in resources
                where res.StartsWith(SpaRoot)
                select res;
            
            var prefix = SpaRoot + ".build.";

            foreach (var res in spaResources)
            {
                var fileName = res[prefix.Length..].Replace('.', '/');
                var lastSlash = fileName.LastIndexOf('/');
                fileName = $"{fileName[..lastSlash]}.{fileName[(lastSlash + 1)..]}";
                
   
                var destFile = Path.Combine(spaRoot, fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

                using var writer = File.OpenWrite(destFile);
                efp.Read(executingAssembly, res).CopyTo(writer);
            }

        }

            

    }
}
