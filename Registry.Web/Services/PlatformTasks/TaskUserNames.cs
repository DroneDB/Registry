using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Identity;

namespace Registry.Web.Services.PlatformTasks;

/// <summary>
/// The user names a task list shows for user ids it stores: who cancelled a task (<c>JobIndex.CancelledBy</c>,
/// D-038). The task records live in the registry database and the users in the identity database, so the names are
/// read in one query per list, not joined.
/// </summary>
public class TaskUserNames
{
    private readonly ApplicationDbContext _users;

    public TaskUserNames(ApplicationDbContext users)
    {
        _users = users;
    }

    /// <summary>The name of each of <paramref name="userIds"/> that exists; an unknown id is left out.</summary>
    public virtual async Task<IReadOnlyDictionary<string, string>> ResolveAsync(IEnumerable<string?> userIds,
        CancellationToken ct = default)
    {
        var ids = userIds.Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var users = await _users.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.UserName })
            .ToArrayAsync(ct);
        return users.ToDictionary(u => u.Id, u => u.UserName ?? u.Id, StringComparer.Ordinal);
    }
}
