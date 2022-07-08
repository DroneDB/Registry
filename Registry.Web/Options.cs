using System.Collections.Generic;
using CommandLine;
using CommandLine.Text;
using SQLitePCL;

namespace Registry.Web;

public class Options
{
    [Value(0, MetaName = "Storage folder",
        HelpText = "Points to a directory on a filesystem where to store Registry data.", Required = true)]
    public string StorageFolder { get; set; }

    [Option('a', "address", Default = "localhost:5000", HelpText = "Address to listen on")]
    public string Address { get; set; }

    [Option('c', "check", HelpText = "Check configuration and exit.", Required = false)]
    public bool CheckConfig { get; set; }
   
    [Option('r', "reset-hub", HelpText = "Reset the Hub folder by re-creating it.", Required = false)]
    public bool ResetHub { get; set; }

    [Usage(ApplicationAlias = "registry.web")]
    public static IEnumerable<Example> Examples =>
        new List<Example>
        {
            new("\nRun Registry using local \"data\" folder as storage on default port 5000",
                new Options { StorageFolder = "./data" }),
            new("\nCheck Registry configuration", new Options { StorageFolder = "./data", CheckConfig = true })
        };
}