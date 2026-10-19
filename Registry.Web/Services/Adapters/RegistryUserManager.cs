#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Identity.Models;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// <see cref="UserManager{TUser}"/> that drops the user's cached authorization snapshot
/// after every role change and deletion, whichever code path makes it (admin API, LDAP
/// sync, deactivation, start-up seeding).
/// </summary>
public class RegistryUserManager : UserManager<User>
{
    private readonly UserAuthSnapshotCache _snapshots;

    public RegistryUserManager(
        IUserStore<User> store,
        IOptions<IdentityOptions> optionsAccessor,
        IPasswordHasher<User> passwordHasher,
        IEnumerable<IUserValidator<User>> userValidators,
        IEnumerable<IPasswordValidator<User>> passwordValidators,
        ILookupNormalizer keyNormalizer,
        IdentityErrorDescriber errors,
        IServiceProvider services,
        ILogger<UserManager<User>> logger,
        UserAuthSnapshotCache snapshots)
        : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors,
            services, logger)
    {
        _snapshots = snapshots;
    }

    public override async Task<IdentityResult> AddToRoleAsync(User user, string role)
        => Invalidated(user, await base.AddToRoleAsync(user, role));

    public override async Task<IdentityResult> AddToRolesAsync(User user, IEnumerable<string> roles)
        => Invalidated(user, await base.AddToRolesAsync(user, roles));

    public override async Task<IdentityResult> RemoveFromRoleAsync(User user, string role)
        => Invalidated(user, await base.RemoveFromRoleAsync(user, role));

    public override async Task<IdentityResult> RemoveFromRolesAsync(User user, IEnumerable<string> roles)
        => Invalidated(user, await base.RemoveFromRolesAsync(user, roles));

    public override async Task<IdentityResult> DeleteAsync(User user)
        => Invalidated(user, await base.DeleteAsync(user));

    // After the store call: a snapshot loaded before the change is discarded (see
    // UserAuthSnapshotCache), one loaded after it reads the new roles.
    private IdentityResult Invalidated(User user, IdentityResult result)
    {
        _snapshots.Invalidate(user.Id);
        return result;
    }
}
