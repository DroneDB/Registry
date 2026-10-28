#nullable disable
using System.Collections.Generic;
using CommandLine;
using CommandLine.Text;

namespace Registry.Web;

public class Options
{
    [Value(0, MetaName = "Storage folder",
        HelpText = "Points to a directory on a filesystem where to store Registry data.", Required = true)]
    public string StorageFolder { get; set; }

    [Option('a', "address", Default = "localhost:5000", HelpText = "Address to listen on")]
    public string Address { get; set; }

    [Option('t', "instance-type", HelpText = "Instance type to run as. (0: Default, 1: ProcessingNode, 2: WebServer, 3: Thumbnail generator)",
        Default = InstanceType.Default, Required = false)]
    public InstanceType InstanceType { get; set; }

    [Option('c', "check", HelpText = "Verify the storage folder's appsettings.json without changing anything and exit (0 = valid).", Required = false)]
    public bool CheckConfig { get; set; }

    [Option("reset-admin-password", HelpText = "Set a new password for the configured default admin and exit. " +
        "The password is read from the " + Program.AdminPasswordEnvironmentVariable + " environment variable or from stdin, never from the command line.",
        Required = false)]
    public bool ResetAdminPassword { get; set; }

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
            new("\nCheck Registry configuration", new Options { StorageFolder = "./data", CheckConfig = true }),
            new("\nReset the default admin password (read from " + Program.AdminPasswordEnvironmentVariable + " or stdin)",
                new Options { StorageFolder = "./data", ResetAdminPassword = true })
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
    WebServer = 2,

    /// <summary>
    /// Run as thumbnail generator only
    /// </summary>
    ThumbnailGenerator = 3
}