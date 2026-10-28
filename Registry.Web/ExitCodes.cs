#nullable disable
namespace Registry.Web;

/// <summary>Process exit codes of Registry.Web (returned by <see cref="Program.Main"/>).</summary>
internal static class ExitCodes
{
    /// <summary>Normal shutdown, a valid <c>--check</c>, a completed maintenance command, help/version.</summary>
    public const int Success = 0;

    /// <summary>Settings, storage, DroneDB or host failure (details on stderr and in the log).</summary>
    public const int Failure = 1;

    /// <summary>The command line could not be parsed or holds an invalid value (e.g. the address).</summary>
    public const int InvalidArguments = 2;
}
