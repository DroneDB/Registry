#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Registry.Web.Data.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Drops the cached dataset lookups (<see cref="IDatasetLookupCache"/>) of every dataset or
/// organization row a <c>SaveChanges</c> modifies or deletes, and of every organization whose members
/// it adds, changes or removes: one hook for every EF write, so no
/// caller has to remember to invalidate. The lookups are dropped before the save and again after
/// it, so a lookup that loads the old row while the save commits cannot keep it.
/// </summary>
public sealed class DatasetChangeInterceptor(IDatasetLookupCache lookups) : SaveChangesInterceptor
{
    // Organization slugs touched by the save in progress, per context (the interceptor is shared)
    private readonly ConditionalWeakTable<DbContext, HashSet<string>> _saving = new();

    private const string All = "*";

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        BeforeSave(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        BeforeSave(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        AfterSave(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        AfterSave(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => AfterSave(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        AfterSave(eventData.Context);
        return Task.CompletedTask;
    }

    private void BeforeSave(DbContext? context)
    {
        if (context == null) return;

        var organizations = new HashSet<string>();
        foreach (var entry in context.ChangeTracker.Entries())
        {
            // Cached lookups carry the organization's members: a new member counts too
            if (entry.Entity is OrganizationUser member &&
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                organizations.Add(member.OrganizationSlug ?? member.Organization?.Slug ?? All);
                continue;
            }

            if (entry.State is not (EntityState.Modified or EntityState.Deleted))
                continue;

            switch (entry.Entity)
            {
                case Organization org:
                    organizations.Add(OriginalSlug(entry) ?? org.Slug);
                    organizations.Add(org.Slug);
                    break;
                case Dataset { Organization: not null } ds:
                    organizations.Add(ds.Organization.Slug);
                    break;
                case Dataset:
                    // Organization not loaded: drop every lookup rather than miss this one
                    organizations.Add(All);
                    break;
            }
        }

        if (organizations.Count == 0) return;
        Invalidate(organizations);
        _saving.AddOrUpdate(context, organizations);
    }

    private void AfterSave(DbContext? context)
    {
        if (context == null || !_saving.TryGetValue(context, out var organizations)) return;
        _saving.Remove(context);
        Invalidate(organizations);
    }

    private void Invalidate(HashSet<string> organizations)
    {
        if (organizations.Contains(All))
        {
            lookups.InvalidateAll();
            return;
        }

        foreach (var org in organizations)
            lookups.InvalidateOrganization(org);
    }

    private static string? OriginalSlug(EntityEntry entry) =>
        entry.Properties.FirstOrDefault(p => p.Metadata.Name == nameof(Organization.Slug))?.OriginalValue as string;
}
