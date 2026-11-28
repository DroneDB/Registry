namespace Registry.Web.Models.Configuration;

/// <summary>
/// The machine and the Registry processes that share it, bound from
/// <c>AppSettings:HostBudget</c>. Every value is optional. With no process declared, each process
/// plans the whole machine, as before this setting existed. A deployment that runs several processes on
/// one machine (the production compose file: a web server and two processing nodes) declares them, and
/// each process takes its share: its weight over the sum of the weights of the declared processes.
/// </summary>
public sealed class HostBudgetSettings
{
    public const double DefaultWebServerWeight = 1;
    public const double DefaultProcessingNodeWeight = 2;
    public const double DefaultThumbnailGeneratorWeight = 1;

    /// <summary>Logical processors of the machine; 0 = the processors this process sees.</summary>
    public int Cores { get; set; }

    /// <summary>Memory of the machine in MiB; 0 = the memory available to this process.</summary>
    public long MemoryMb { get; set; }

    /// <summary>Web server processes on the machine (0 = not declared).</summary>
    public int WebServers { get; set; }

    /// <summary>Processing node processes on the machine (0 = not declared).</summary>
    public int ProcessingNodes { get; set; }

    /// <summary>Thumbnail generator processes on the machine (0 = not declared).</summary>
    public int ThumbnailGenerators { get; set; }

    /// <summary>Relative share of a web server. Default: 1.</summary>
    public double WebServerWeight { get; set; } = DefaultWebServerWeight;

    /// <summary>Relative share of a processing node. Default: 2.</summary>
    public double ProcessingNodeWeight { get; set; } = DefaultProcessingNodeWeight;

    /// <summary>Relative share of a thumbnail generator. Default: 1.</summary>
    public double ThumbnailGeneratorWeight { get; set; } = DefaultThumbnailGeneratorWeight;

    /// <summary>
    /// Disk-bound threads (hashing, deleting build folders) for the whole machine, split like the cores
    /// (at least 1 per process); 0 = libddb's default in each process (4).
    /// </summary>
    public int IoThreads { get; set; }

    /// <summary>True when the deployment declared at least one process or the machine's size.</summary>
    public bool IsDeclared => WebServers > 0 || ProcessingNodes > 0 || ThumbnailGenerators > 0 || Cores > 0 ||
                              MemoryMb > 0 || IoThreads > 0;
}
