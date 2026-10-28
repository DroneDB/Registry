#nullable enable
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that makes the <c>.spz</c> of a Gaussian splat for other tools (<c>DDBExportSplatSpz</c>). libddb
/// keeps the file in the build folder of the entry as the artifact <c>gsplat.export</c> and returns it as it is while
/// it is not older than the dataset file, so a second export is immediate. The task has no result of its own: the
/// client downloads the artifact with <c>GET build-artifact?path=...&amp;role=gsplat.export</c>, which names it after the
/// entry. Read access, like the other exports: it re-packages a file the caller can already download.
/// </summary>
/// <remarks>
/// libddb holds the build lock of the entry while it converts, so an export started while a build (or another export)
/// of the entry runs fails with <see cref="DdbBuildInProgressException"/>. That is not an error of the request: the
/// task is a <see cref="Jobs.DdbRetriedJob"/>, which waits for the lock with its backoff chain, the one policy of every
/// job that needs the build lock.
/// </remarks>
public sealed class SplatSpzExportTool : HeavyToolBase<SplatSpzExportTool.Parameters>
{
    /// <summary>The tool has no parameters beyond the entry path.</summary>
    public sealed record Parameters;

    protected override Parameters ReadParams(ToolParameters p) => new();

    private static readonly JsonDocument Schema = JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["path"],
          "properties": {
            "path": { "type": "string", "description": "Entry path of the built Gaussian splat (PLY, SPLAT or SPZ)." }
          },
          "additionalProperties": false
        }
        """);

    public override string Id => "splat-spz-export";
    public override string Version => "1";
    public override string Title => "Export splat as .spz";
    public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Read;

    // The file stays in the build folder of the entry (libddb's cache), not in a work folder of the task
    public override bool ProducesArtifact => false;

    // The conversion decodes the whole file in memory: it shares the builds queue with the other native work,
    // whose worker count bounds how many run at once
    public override bool RunsNativeBuild => true;
    public override JsonDocument InputSchema => Schema;

    protected override Task ValidateAsync(HeavyToolRequest request, Parameters p, IHeavyToolValidationContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            throw new ArgumentException("A splat 'path' is required.");

        var build = ctx.Ddb.GetBuildArtifacts([request.Path])[0];
        if (!build.Exists)
            throw new NotFoundException($"Splat '{request.Path}' does not exist in the dataset.");
        if (build.Type != EntryType.GaussianSplat)
            throw new BadRequestException($"'{request.Path}' is not a Gaussian splat.");

        // The streamed model is what a finished build leaves: libddb exports only a built splat
        if (!build.IsOwnBuild() || build.Build!.Find(ArtifactRoles.GsplatLod) == null)
            throw new NotFoundException(
                $"Splat '{request.Path}' is not built yet: export it when its build is complete.");

        return Task.CompletedTask;
    }

    protected override HeavyToolPlan Plan(HeavyToolRequest request, Parameters p, IHeavyToolValidationContext ctx) =>
        new(null, QuotaKey: Id, DefaultFileName: null, ContentType: null);

    protected override async Task<HeavyToolArtifact?> ExecuteAsync(HeavyToolRequest request, Parameters p,
        IHeavyToolExecutionContext ctx, IProgress<HeavyToolProgress> progress, CancellationToken ct)
    {
        progress.Report(new HeavyToolProgress(-1, "exporting", LogChunk: $"Exporting '{request.Path}' as a .spz file"));

        var export = await progress.ReportingFailureAsync(
            () => Task.Run(() => ctx.Ddb.ExportSplatSpz(request.Path!, ct), ct));

        progress.Report(new HeavyToolProgress(1, "done",
            LogChunk: export.Cached ? "The .spz file was already exported" : "Export complete"));
        return null;
    }
}
