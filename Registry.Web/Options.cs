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

    [Option('t', "instance-type", HelpText = "Instance type to run as. (0: Default, 1: ProcessingNode, 2: WebServer)",
        Default = InstanceType.Default, Required = false)]
    public InstanceType InstanceType { get; set; }

    [Option('c', "check", HelpText = "Check configuration and exit.", Required = false)]
    public bool CheckConfig { get; set; }

    [Option('r', "reset-hub", HelpText = "Reset the Hub folder by re-creating it.", Required = false)]
    public bool ResetHub { get; set; }

    [Option('d', "reset-ddb", HelpText = "Reset the ddb folder by re-creating it.", Required = false)]
    public bool ResetDdb { get; set; }

    [Usage(ApplicationAlias = "registry.web")]
    public static IEnumerable<Example> Examples =>
        new List<Example>
        {
            new("\nRun Registry using local \"data\" folder as storage on default port 5000",
                new Options { StorageFolder = "./data" }),
            new("\nCheck Registry configuration", new Options { StorageFolder = "./data", CheckConfig = true })
        };
}

/// <summary>
/// Type of instance to run
/// </summary>
public enum InstanceType
{
    /// <summary>
    /// Default behavior: run as both web server and processing node
    /// </summary>
    Default = 0,

    /// <summary>
    /// Run as processing node only
    /// </summary>
    ProcessingNode = 1,

    /// <summary>
    /// Run as web server only
    /// </summary>
    WebServer = 2
}