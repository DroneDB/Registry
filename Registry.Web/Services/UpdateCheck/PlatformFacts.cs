using System;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Registry.Ports;

namespace Registry.Web.Services.UpdateCheck;

/// <summary>What the machine and the runtime say about themselves, as they say it.</summary>
/// <param name="Os"><c>windows</c> or <c>linux</c>; null on another system.</param>
/// <param name="OsDescription">The operating system description.</param>
/// <param name="Architecture">The architecture, lowercase (<c>x64</c>, <c>arm64</c>, <c>x86</c>, <c>arm</c>, or what the runtime names another).</param>
/// <param name="FrameworkDescription">The .NET runtime description.</param>
/// <param name="InContainer">The process runs in a container (Docker, Kubernetes, containerd).</param>
/// <param name="HostName">The host name of the machine.</param>
/// <param name="DomainName">The network or Active Directory domain of the machine, if it has one.</param>
public sealed record PlatformSnapshot(
    string? Os,
    string? OsDescription,
    string? Architecture,
    string? FrameworkDescription,
    bool InContainer,
    string? HostName,
    string? DomainName);

/// <summary>Reads the facts of the machine the update check reports.</summary>
public interface IPlatformFacts
{
    /// <summary>The facts of this machine, now.</summary>
    PlatformSnapshot Capture();
}

/// <summary><see cref="IPlatformFacts"/> of the machine this process runs on.</summary>
public sealed partial class PlatformFacts : IPlatformFacts
{
    private const string DockerMarkerFile = "/.dockerenv";
    private const string ControlGroupFile = "/proc/1/cgroup";

    private readonly IFileSystem _fs;

    public PlatformFacts(IFileSystem fs) => _fs = fs;

    [GeneratedRegex("docker|kubepods|containerd", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContainerControlGroup();

    /// <inheritdoc />
    public PlatformSnapshot Capture() => new(
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : null,
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        RuntimeInformation.FrameworkDescription,
        InContainer(),
        HostName(),
        DomainName());

    private bool InContainer()
    {
        if (!OperatingSystem.IsLinux())
            return false;

        try
        {
            return _fs.Exists(DockerMarkerFile) ||
                   _fs.Exists(ControlGroupFile) && ContainerControlGroup().IsMatch(_fs.ReadAllText(ControlGroupFile));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // A machine that cannot say is reported as not saying: the update check never fails for it
    private static string? HostName()
    {
        try
        {
            return Dns.GetHostName();
        }
        catch (SocketException)
        {
            return null;
        }
    }

    // The DNS domain (Linux answers "(none)" for a machine without one), else the Active Directory one that Windows sets
    // in the environment
    private static string? DomainName()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().DomainName is { Length: > 0 } domain && domain != "(none)"
                ? domain
                : Environment.GetEnvironmentVariable("USERDNSDOMAIN");
        }
        catch (NetworkInformationException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }
}
