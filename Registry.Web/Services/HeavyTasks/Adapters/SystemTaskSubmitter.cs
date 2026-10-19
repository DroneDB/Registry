#nullable enable
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>Default <see cref="ISystemTaskSubmitter"/>.</summary>
public sealed class SystemTaskSubmitter : ISystemTaskSubmitter
{
    private readonly IHeavyToolRegistry _registry;
    private readonly IBackgroundJobsProcessor _processor;
    private readonly ILogger<SystemTaskSubmitter> _log;

    public SystemTaskSubmitter(IHeavyToolRegistry registry, IBackgroundJobsProcessor processor,
        ILogger<SystemTaskSubmitter> log)
    {
        _registry = registry;
        _processor = processor;
        _log = log;
    }

    public async Task<string> SubmitAsync(string toolId, string orgSlug, string dsSlug, IDDB ddb, string? path,
        JsonElement prms, string? userId = null, CancellationToken ct = default)
    {
        var tool = _registry.Resolve(toolId)
                   ?? throw new HeavyToolNotFoundException($"Tool '{toolId}' is not available.");

        var request = new HeavyToolRequest(tool.Id, tool.Version, orgSlug, dsSlug, path, prms);
        await tool.ValidateAsync(request, new HeavyToolValidationContext(ddb, null, _log), ct);

        return _processor.EnqueueHeavyTask(request, userId,
            requestHash: HeavyTaskRequestHash.Compute(tool.Id, tool.Version, null, prms));
    }
}
