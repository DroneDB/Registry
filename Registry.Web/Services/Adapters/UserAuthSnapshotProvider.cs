using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Loads <see cref="UserAuthSnapshot"/>s from the identity store through the shared
/// <see cref="UserAuthSnapshotCache"/>: one user lookup and two role checks on a miss,
/// nothing on a hit. Scoped: within one request a user's snapshot is read once, also when
/// the shared cache is disabled, until it is invalidated.
/// </summary>
public class UserAuthSnapshotProvider : IUserAuthSnapshotProvider
{
    private readonly UserManager<User> _userManager;
    private readonly UserAuthSnapshotCache _cache;
    private readonly Dictionary<string, (long Generation, Task<UserAuthSnapshot> Snapshot)> _scope = new();

    public UserAuthSnapshotProvider(UserManager<User> userManager, UserAuthSnapshotCache cache)
    {
        _userManager = userManager;
        _cache = cache;
    }

    public Task<UserAuthSnapshot> GetAsync(string userId)
    {
        var generation = _cache.GenerationOf(userId);
        if (_scope.TryGetValue(userId, out var known) && known.Generation == generation)
            return known.Snapshot;

        var snapshot = _cache.GetOrLoadAsync(userId, () => LoadAsync(userId));
        _scope[userId] = (generation, snapshot);
        return snapshot;
    }

    private async Task<UserAuthSnapshot> LoadAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            return UserAuthSnapshot.Missing;

        return new UserAuthSnapshot(
            true,
            user.UserName,
            await _userManager.IsInRoleAsync(user, ApplicationDbContext.AdminRoleName),
            await _userManager.IsInRoleAsync(user, ApplicationDbContext.DeactivatedRoleName));
    }
}
