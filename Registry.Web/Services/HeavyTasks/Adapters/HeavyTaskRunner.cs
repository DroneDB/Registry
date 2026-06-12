#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// Default <see cref="IHeavyTaskRunner"/>: resolves a native tool, validates and
/// plans the request, deduplicates, enforces quota and enqueues the
/// <see cref="HeavyTaskJobWrapper"/> on the <c>tasks</c> queue (spec §4).
/// </summary>
public sealed class HeavyTaskRunner : IHeavyTaskRunner
{
    private const string TasksQueue = "tasks";

    private readonly IHeavyToolRegistry _registry;
    private readonly IUtils _utils;
    private readonly IDdbManager _ddbManager;
    private readonly IHeavyTaskQuota _quota;
    private readonly IJobIndexQuery _query;
    private readonly IBackgroundJobsProcessor _processor;
    private readonly ProcessingPlatformSettings _settings;
    private readonly ILogger<HeavyTaskRunner> _log;

    public HeavyTaskRunner(
        IHeavyToolRegistry registry,
        IUtils utils,
        IDdbManager ddbManager,
        IHeavyTaskQuota quota,
        IJobIndexQuery query,
        IBackgroundJobsProcessor processor,
        IOptions<AppSettings> appSettings,
        ILogger<HeavyTaskRunner> log)
    {
        _registry = registry;
        _utils = utils;
        _ddbManager = ddbManager;
        _quota = quota;
        _query = query;
        _processor = processor;
        _settings = appSettings.Value.ProcessingPlatform ?? new ProcessingPlatformSettings();
        _log = log;
    }

    public async Task<HeavyTaskSubmitResult> SubmitAsync(HeavyTaskSubmitRequest request, CancellationToken ct = default)
    {
        
        var tool = _registry.Resolve(request.ToolId, request.Version)
                   ?? throw new HeavyToolNotFoundException($"Tool '{request.ToolId}' is not available.");

        var ds = _utils.GetDataset(request.OrgSlug, request.DsSlug);
        
        var ddb = _ddbManager.Get(request.OrgSlug, ds.InternalRef);

        var toolRequest = new HeavyToolRequest(
            tool.Id, tool.Version, request.OrgSlug, request.DsSlug, request.Path, request.Params);

        var validationCtx = new HeavyToolValidationContext(ddb, request.Caller, _log);

        await tool.ValidateAsync(toolRequest, validationCtx, ct);
        var plan = tool.Plan(toolRequest, validationCtx);

        var requestHash = ComputeRequestHash(tool.Id, tool.Version, request.Hash, request.Params);

        // Dedup: workflow sub-tasks always force (never reuse a pre-existing terminal task).
        if (_settings.DedupEnabled && !request.Force)
        {
            var candidate = await _query.FindDedupCandidateAsync(
                request.OrgSlug, request.DsSlug, tool.Id, requestHash, _settings.DedupLookbackHours, ct);

            if (candidate is not null)
            {
                _log.LogInformation("Deduplicated task submit for tool {ToolId} -> {JobId}", tool.Id, candidate.JobId);
                return new HeavyTaskSubmitResult(candidate.JobId, tool.Id, tool.Version, true, plan.EstimatedOutputBytes);
            }
        }

        var quotaResult = await _quota.EvaluateAsync(request, plan, ct);
        if (!quotaResult.IsAllowed)
            throw new HeavyTaskQuotaException(quotaResult.Code, quotaResult.Message ?? "Quota exceeded.");

        var requestJson = JsonSerializer.Serialize(toolRequest);
        var version = tool.Version;
        var toolId = tool.Id;

        var payload = new IndexPayload(
            request.OrgSlug, request.DsSlug, request.Hash, request.UserId,
            Queue: TasksQueue, Path: request.Path,
            ToolId: toolId, ToolVersion: version,
            RequestHash: requestHash,
            ParentJobId: request.ParentJobId,
            WorkflowExecutionId: request.WorkflowExecutionId);

        var jobId = _processor.EnqueueIndexed<HeavyTaskJobWrapper>(
            w => w.Run(toolId, version, requestJson, null!, CancellationToken.None), payload);

        return new HeavyTaskSubmitResult(jobId, toolId, version, false, plan.EstimatedOutputBytes);
    }

    public string SubmitSystemBuild(string orgSlug, string dsSlug, string? path, bool force, string? hash = null)
    {
        const string toolId = "build";
        const string version = "1";

        var prms = JsonSerializer.SerializeToElement(new { path, force });
        var toolRequest = new HeavyToolRequest(toolId, version, orgSlug, dsSlug, path, prms);
        var requestJson = JsonSerializer.Serialize(toolRequest);

        var payload = new IndexPayload(
            orgSlug, dsSlug, hash, UserId: null,
            Queue: TasksQueue, Path: path,
            ToolId: toolId, ToolVersion: version);

        return _processor.EnqueueIndexed<HeavyTaskJobWrapper>(
            w => w.Run(toolId, version, requestJson, null!, CancellationToken.None), payload);
    }

    private static string ComputeRequestHash(string toolId, string version, string? entryHash, JsonElement prms)
    {
        var canonical = $"{toolId}|{version}|{entryHash}|{CanonicalJson(prms)}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string CanonicalJson(JsonElement element)
    {
        // Guard: Undefined means the field was absent from the JSON body.
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return "null";

        // Stable serialization for dedup: sort object keys recursively.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(element, writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var props = new System.Collections.Generic.SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var p in element.EnumerateObject())
                    props[p.Name] = p.Value;
                foreach (var kv in props)
                {
                    writer.WritePropertyName(kv.Key);
                    WriteCanonical(kv.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
