#nullable enable
namespace Registry.Ports.Import;

/// <summary>
/// Result of an import-source probe (the synchronous "verify" step): whether the source is
/// reachable with the supplied credentials, plus best-effort size/count hints for the UI and the
/// pre-flight quota gate.
/// </summary>
/// <param name="Reachable">True when the source responded and the credentials were accepted.</param>
/// <param name="Message">Human-readable status or error message.</param>
/// <param name="EstimatedBytes">Total size estimate when it can be obtained cheaply, otherwise null.</param>
/// <param name="FileCount">Number of files discovered, when known.</param>
/// <param name="SuggestedName">A suggested dataset name (e.g. the remote dataset slug), when available.</param>
public sealed record ImportSourceProbe(
    bool Reachable,
    string? Message,
    long? EstimatedBytes,
    int? FileCount,
    string? SuggestedName);

/// <summary>
/// Incremental progress emitted by an import source during <see cref="IImportSource.FetchAsync"/>.
/// </summary>
/// <param name="Fraction">Completion fraction in 0..1, or -1 for indeterminate.</param>
/// <param name="Phase">Coarse phase label (e.g. "connecting", "listing", "downloading", "done").</param>
/// <param name="Message">Optional human-readable detail.</param>
/// <param name="BytesSoFar">
/// Running total of bytes written during this run. The tool enforces the size/quota budget against
/// it and derives throughput/ETA from it.
/// </param>
/// <param name="FilesDone">Files completed so far, when known.</param>
/// <param name="FilesTotal">Total files to transfer, when known.</param>
public sealed record ImportProgress(
    double Fraction,
    string? Phase = null,
    string? Message = null,
    long? BytesSoFar = null,
    int? FilesDone = null,
    int? FilesTotal = null);
