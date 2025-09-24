using System;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Client;
using Hangfire.Server;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

#nullable enable

public class JobIndexWriter(RegistryContext db, ILogger<JobIndexWriter> log) : IJobIndexWriter
{
    public async Task UpsertOnEnqueueAsync(string jobId, IndexPayload meta, DateTime createdAtUtc,
        string? methodDisplay, CancellationToken ct = default)
    {
        meta.EnsureValid();

        var existing = await db.JobIndices.AsTracking().FirstOrDefaultAsync(x => x.JobId == jobId, ct);
        if (existing is null)
        {
            db.JobIndices.Add(new JobIndex
            {
                JobId = jobId,
                OrgSlug = meta.OrgSlug,
                DsSlug = meta.DsSlug,
                Path = meta.Path,
                UserId = meta.UserId,
                Queue = meta.Queue,
                CreatedAtUtc = createdAtUtc,
                LastStateChangeUtc = createdAtUtc,
                CurrentState = "Created",
                MethodDisplay = methodDisplay
            });
        }
        else
        {
            existing.OrgSlug = meta.OrgSlug;
            existing.DsSlug = meta.DsSlug;
            existing.Path = meta.Path;
            existing.UserId = meta.UserId;
            existing.Queue = meta.Queue ?? existing.Queue;
            existing.MethodDisplay = methodDisplay ?? existing.MethodDisplay;
            existing.CreatedAtUtc = existing.CreatedAtUtc == default ? createdAtUtc : existing.CreatedAtUtc;
            existing.LastStateChangeUtc = createdAtUtc;
            existing.CurrentState = "Created";
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateStateAsync(string jobId, string newState, DateTime changedAtUtc,
        CancellationToken ct = default)
    {
        var ji = await db.JobIndices.AsTracking().FirstOrDefaultAsync(x => x.JobId == jobId, ct);
        if (ji is null)
        {
            // Doesn't exist? Create a shell record, better than losing the state
            ji = new JobIndex
            {
                JobId = jobId,
                OrgSlug = "~unknown~",
                DsSlug = "~unknown~",
                CreatedAtUtc = changedAtUtc,
            };
            db.JobIndices.Add(ji);
        }

        ji.CurrentState = newState;
        ji.LastStateChangeUtc = changedAtUtc;
        if (newState == ProcessingState.StateName)
            ji.ProcessingAtUtc = changedAtUtc;
        else if (newState == SucceededState.StateName)
            ji.SucceededAtUtc = changedAtUtc;
        else if (newState == FailedState.StateName)
            ji.FailedAtUtc = changedAtUtc;
        else if (newState == DeletedState.StateName)
            ji.DeletedAtUtc = changedAtUtc;
        else if (newState == ScheduledState.StateName)
            ji.ScheduledAtUtc = changedAtUtc;

        await db.SaveChangesAsync(ct);
    }
}

// -------------------------------------------------------------
// 7) Usage examples in the app
// -------------------------------------------------------------
/*
public static class Examples
{
    // 7.1) Registration in Program.cs / Startup.cs
    public static void ConfigureServices(IServiceCollection services, string connString, bool useSqlServer)
    {
        // EF Core for the index (choose your preferred provider)
        services.AddJobIndexing(db =>
        {
            if (useSqlServer)
                db.UseSqlServer(connString);
            else
                db.UseMySql(connString, ServerVersion.AutoDetect(connString));
            // In development you can also use .UseSqlite("Data Source=jobindex.db");
        });

        // Hangfire (ASP.NET Core): register the global filter correctly
        services.AddHangfire((sp, cfg) =>
        {
            cfg.UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings();

            var logger = sp.GetRequiredService<ILogger<JobIndexStateFilter>>();
            cfg.UseFilter(new JobIndexStateFilter(sp, logger)); // <— no JobFilterDescriptor needed
        });

        services.AddHangfireServer();
    }

// 7.2) Enqueue a job with indexing (in your AddNew service)
    public static string EnqueueBuild(IIndexedJobEnqueuer enqueuer, string org, string ds, string path, string? userId)
    {
        // EXAMPLE: call a static job; you can also use instance methods
        return enqueuer.Enqueue(() => HangfireUtils.BuildWrapper(org, ds, path),
            new IndexPayload(org, ds, path, userId));
    }

    // 7.3) Query (for org/ds and for org/ds/path)
    public static async Task DemoQueriesAsync(IJobIndexQuery query)
    {
        var jobs1 = await query.GetByOrgDsAsync("acme", "buildings");
        var jobs2 = await query.GetByOrgDsPathAsync("acme", "buildings", "/tiles/index.json", prefix: false);
        var jobs3 = await query.GetByOrgDsPathAsync("acme", "buildings", "/tiles/", prefix: true);
        // use the results as you prefer (UI, API, etc.)
    }
}

// -------------------------------------------------------------
// 8) Your job (example) — avoid passing complex services/objects in parameters
// -------------------------------------------------------------

public static class HangfireUtils
{
    // Example of "clean" signature: only slug/path, retrieve dependencies via DI
    public static async Task BuildWrapper(string orgSlug, string dsSlug, string path)
    {
        // TODO: resolve services from your IoC container (JobActivator.Current), execute the build, etc.
        await Task.CompletedTask;
    }
}*/