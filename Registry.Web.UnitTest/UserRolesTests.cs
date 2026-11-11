using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Users;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Registry.Web.Test;

/// <summary>
/// FR-ADM-04 and D-043 on a real identity store (SQLite): the system roles cannot be deleted; Edit User changes only
/// the roles that differ, checks every name first, grants and removes <c>admin</c> and never touches
/// <c>deactivated</c>; disable and enable add and remove <c>deactivated</c>, also for a user of an external provider,
/// and the cached authorization of the user is dropped at once.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UserRolesTests"
/// </summary>
[TestFixture]
public class UserRolesTests
{
    private const string Admin = ApplicationDbContext.AdminRoleName;
    private const string Deactivated = ApplicationDbContext.DeactivatedRoleName;

    private TestDb<ApplicationDbContext> _db = null!;
    private HookedUserManager _users = null!;
    private RoleManager<IdentityRole> _roleManager = null!;
    private UserAuthSnapshotCache _snapshots = null!;
    private Mock<ILoginManager> _login = null!;
    private Mock<IAuthManager> _auth = null!;
    private UserRoles _roles = null!;
    private UserProvisioning _provisioning = null!;
    private UserAccounts _accounts = null!;

    [SetUp]
    public async Task SetUp()
    {
        _db = TestDb.Create<ApplicationDbContext>();
        var context = _db.NewContext();
        _snapshots = new UserAuthSnapshotCache(new MemoryCache(new MemoryCacheOptions()), TimeSpan.FromMinutes(5));
        _users = new HookedUserManager(new UserStore<User, IdentityRole, ApplicationDbContext>(context),
            MsOptions.Create(new IdentityOptions()), new PasswordHasher<User>(), [], [],
            new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
            NullLogger<UserManager<User>>.Instance, _snapshots);
        _roleManager = new RoleManager<IdentityRole>(new RoleStore<IdentityRole>(context), [],
            new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), NullLogger<RoleManager<IdentityRole>>.Instance);

        _login = new Mock<ILoginManager>();
        _login.Setup(l => l.Capabilities).Returns(AuthProviderCapabilities.Local);
        _auth = new Mock<IAuthManager>();

        var policy = new Mock<IPasswordPolicyValidator>();
        policy.Setup(p => p.Validate(It.IsAny<string>())).Returns(PasswordValidationResult.Success());
        _provisioning = new UserProvisioning(_users, _roleManager, context, policy.Object,
            TestSettings.Options<AccountOptions>(TestSettings.Default),
            new Mock<IOrganizationsManager>().Object, NullLogger<UserProvisioning>.Instance);
        _roles = new UserRoles(_login.Object, _users, _roleManager, _provisioning, _auth.Object);
        _accounts = new UserAccounts(_login.Object, _users, _provisioning, _auth.Object, context, null!,
            NullLogger<UserAccounts>.Instance);

        foreach (var role in new[] { Admin, Deactivated, "editor", "viewer", "pilot" })
            (await _roleManager.CreateAsync(new IdentityRole(role))).Succeeded.ShouldBeTrue();

        await CreateUser("boss", Admin);
        CallerIs("boss");
    }

    [TearDown]
    public void TearDown()
    {
        _users.Dispose();
        _roleManager.Dispose();
        _db.Dispose();
    }

    private async Task<User> CreateUser(string userName, params string[] roles)
    {
        var user = new User { UserName = userName, Email = $"{userName}@example.com" };
        (await _users.CreateAsync(user)).Succeeded.ShouldBeTrue();
        foreach (var role in roles)
            (await _users.AddToRoleAsync(user, role)).Succeeded.ShouldBeTrue();
        return user;
    }

    private void CallerIs(string userName)
    {
        _auth.Setup(a => a.GetCurrentUserId()).ReturnsAsync(() => _users.Users.Single(u => u.UserName == userName).Id);
        _auth.Setup(a => a.GetCurrentUser()).ReturnsAsync(() => _users.Users.Single(u => u.UserName == userName));
    }

    private async Task<string[]> RolesOf(string userName) =>
        [.. (await _users.GetRolesAsync((await _users.FindByNameAsync(userName))!)).OrderBy(r => r)];

    #region DeleteRole

    [TestCase("deactivated")]
    [TestCase("Deactivated")]
    [TestCase("admin")]
    [TestCase("ADMIN")]
    public async Task DeleteRole_ASystemRole_Is400_InAnyCase_AndTheRoleStays(string role)
    {
        var ex = await Should.ThrowAsync<BadRequestException>(() => _roles.DeleteRole(role));

        ex.StatusCode.ShouldBe(400);
        ex.Message.ShouldContain("system role");
        (await _roleManager.RoleExistsAsync(role)).ShouldBeTrue();
    }

    [Test]
    public async Task DeleteRole_ACustomRole_IsDeleted()
    {
        await _roles.DeleteRole("pilot");

        (await _roleManager.RoleExistsAsync("pilot")).ShouldBeFalse();
    }

    #endregion

    #region CreateRole and the delete of a legacy role (FR-ADM-01)

    // The role created, listed and deleted by one name rule
    [TestCase("a/b")]
    [TestCase("Bad Role!")]
    [TestCase("a-b")]
    [TestCase("Editor2")]
    [TestCase("a.b")]
    public async Task CreateRole_ANameOutsideTheRule_Is400AtTheRoleNameField_AndMakesNoRole(string name)
    {
        var ex = await Should.ThrowAsync<InvalidFieldException>(() => _roles.CreateRole(name));

        ex.StatusCode.ShouldBe(400);
        ex.Message.ShouldBe("Role name is invalid: use 1 to 127 characters: lowercase letters (a–z) and digits (0–9)");
        ex.FieldErrors!.Keys.ShouldBe(["roleName"]);
        (await _roleManager.RoleExistsAsync(name)).ShouldBeFalse();
    }

    [Test]
    public async Task CreateRole_ANameThatFollowsTheRule_IsCreated()
    {
        await _roles.CreateRole("surveyors2");

        (await _roleManager.RoleExistsAsync("surveyors2")).ShouldBeTrue();
        await Should.ThrowAsync<InvalidFieldException>(() => _roles.CreateRole(new string('a', 128)));
        await _roles.CreateRole(new string('a', 127));
    }

    // A role from before the rule: listed, so it can be deleted, also through the percent-encoded route value
    [TestCase("a/b", "a%2Fb")]
    [TestCase("a/b", "a/b")]
    [TestCase("Bad Role!", "Bad Role!")]
    [TestCase("Bad Role!", "Bad%20Role!")]
    [TestCase("100%", "100%")]
    public async Task DeleteRole_ALegacyRoleWithASlashOrASign_IsDeleted(string stored, string routeValue)
    {
        (await _roleManager.CreateAsync(new IdentityRole(stored))).Succeeded.ShouldBeTrue();

        await _roles.DeleteRole(routeValue);

        (await _roleManager.RoleExistsAsync(stored)).ShouldBeFalse();
    }

    [Test]
    public async Task DeleteRole_ASystemRoleBehindAPercentEscape_Is400()
    {
        await Should.ThrowAsync<BadRequestException>(() => _roles.DeleteRole("%61dmin"));

        (await _roleManager.RoleExistsAsync(Admin)).ShouldBeTrue();
    }

    #endregion

    #region Add User and Edit User names and e-mail

    [TestCase("t-admin-user")]
    [TestCase("TADMINUPPER")]
    [TestCase("bad name!")]
    [TestCase("")]
    [TestCase("a.b")]
    [TestCase("bob\n")]
    public async Task CreateUser_ANameOutsideTheRule_Is400AtTheUserNameField(string name)
    {
        var ex = await Should.ThrowAsync<InvalidFieldException>(() => _accounts.CreateUser(name, null, "Password1", null));

        ex.Message.ShouldBe("User name is invalid: use 1 to 127 characters: lowercase letters (a–z) and digits (0–9)");
        ex.FieldErrors!.Keys.ShouldBe(["userName"]);
    }

    [Test]
    public async Task CreateUser_ANameThatExists_Is409AtTheUserNameField()
    {
        var ex = await Should.ThrowAsync<ConflictException>(() => _accounts.CreateUser("boss", null, "Password1", null));

        ex.StatusCode.ShouldBe(409);
        ex.FieldErrors!["userName"].ShouldBe(["User already exists"]);
    }

    [Test]
    public async Task CreateUser_AnEmailThatIsNotAnAddress_Is400AtTheEmailField()
    {
        var ex = await Should.ThrowAsync<InvalidFieldException>(() => _accounts.CreateUser("bob", "not an email", "Password1", null));

        ex.Message.ShouldBe(InputRules.InvalidEmailMessage);
        ex.FieldErrors!.Keys.ShouldBe(["email"]);
    }

    // FR-ADM-07: the e-mail is optional in Edit User
    [TestCase("")]
    [TestCase("   ")]
    public async Task UpdateUser_AnEmptyEmail_RemovesTheAddress(string email)
    {
        await CreateUser("pat");

        await _accounts.UpdateUser("pat", email);

        var pat = await _users.FindByNameAsync("pat");
        pat!.Email.ShouldBeNull();
        pat.NormalizedEmail.ShouldBeNull();
    }

    [Test]
    public async Task UpdateUser_AnOmittedEmail_KeepsTheAddress()
    {
        await CreateUser("pat");

        await _accounts.UpdateUser("pat", null);

        (await _users.FindByNameAsync("pat"))!.Email.ShouldBe("pat@example.com");
    }

    [Test]
    public async Task UpdateUser_AnEmailThatIsNotAnAddress_Is400AtTheEmailField_AndKeepsTheAddress()
    {
        await CreateUser("pat");

        var ex = await Should.ThrowAsync<InvalidFieldException>(() => _accounts.UpdateUser("pat", "not an email"));

        ex.FieldErrors!.Keys.ShouldBe(["email"]);
        (await _users.FindByNameAsync("pat"))!.Email.ShouldBe("pat@example.com");
    }

    [Test]
    public async Task UpdateUser_ANewAddress_IsStoredTrimmed_WithItsNormalizedForm()
    {
        await CreateUser("pat");

        await _accounts.UpdateUser("pat", " Pat.New@Example.com ");

        var pat = await _users.FindByNameAsync("pat");
        pat!.Email.ShouldBe("Pat.New@Example.com");
        pat.NormalizedEmail.ShouldBe("PAT.NEW@EXAMPLE.COM");
    }

    #endregion

    #region UpdateUserRoles (Edit User)

    [Test]
    public async Task UpdateUserRoles_WithoutDeactivated_KeepsTheUserDisabled()
    {
        await CreateUser("jane", "editor", Deactivated);

        await _roles.UpdateUserRoles("jane", ["viewer"]);

        (await RolesOf("jane")).ShouldBe([Deactivated, "viewer"]);
    }

    [Test]
    public async Task UpdateUserRoles_WithDeactivated_Is400_AndDoesNotDisableTheUser()
    {
        await CreateUser("jane", "editor");

        var ex = await Should.ThrowAsync<BadRequestException>(() => _roles.UpdateUserRoles("jane", ["viewer", Deactivated]));

        ex.Message.ShouldBe(SystemRoles.DeactivatedInRoleListMessage);
        (await RolesOf("jane")).ShouldBe(["editor"]);
    }

    // A client that sends a disabled user's roles back: 'deactivated' adds nothing, the user stays disabled
    [Test]
    public async Task UpdateUserRoles_ADisabledUsersListWithDeactivated_IsAccepted_AndKeepsThemDisabled()
    {
        await CreateUser("jane", "editor", Deactivated);

        await _roles.UpdateUserRoles("jane", ["viewer", Deactivated]);

        (await RolesOf("jane")).ShouldBe([Deactivated, "viewer"]);
    }

    [Test]
    public async Task UpdateUserRoles_WithAMistypedRole_Is400_AndChangesNothing()
    {
        await CreateUser("jane", "editor", "viewer");

        var ex = await Should.ThrowAsync<BadRequestException>(() => _roles.UpdateUserRoles("jane", ["pilot", "edtior"]));

        ex.Message.ShouldContain("edtior");
        (await RolesOf("jane")).ShouldBe(["editor", "viewer"]);
    }

    [Test]
    public async Task UpdateUserRoles_WithAndWithoutAdmin_GrantsAndRemovesAdmin()
    {
        await CreateUser("jane", "editor");

        await _roles.UpdateUserRoles("jane", ["editor", Admin]);
        (await RolesOf("jane")).ShouldBe([Admin, "editor"]);

        await _roles.UpdateUserRoles("jane", ["editor"]);
        (await RolesOf("jane")).ShouldBe(["editor"]);
    }

    [Test]
    public async Task UpdateUserRoles_ChangesOnlyTheDifference_InAnyCase_WithRepeats()
    {
        await CreateUser("jane", "editor", "viewer");

        await _roles.UpdateUserRoles("jane", ["Editor", "editor", "pilot"]);

        (await RolesOf("jane")).ShouldBe(["editor", "pilot"]);
    }

    [Test]
    public async Task UpdateUserRoles_TheAdminUser_KeepsTheAdminRole()
    {
        await CreateUser("admin", Admin);

        await Should.ThrowAsync<BadRequestException>(() => _roles.UpdateUserRoles("admin", ["editor"]));

        (await RolesOf("admin")).ShouldBe([Admin]);
    }

    #endregion

    #region DisableUser / EnableUser

    [Test]
    public async Task DisableUser_AddsDeactivated_AndTheCachedAuthorizationIsDroppedAtOnce()
    {
        var jane = await CreateUser("jane", "editor");
        var provider = new UserAuthSnapshotProvider(_users, _snapshots);
        (await provider.GetAsync(jane.Id)).IsActive.ShouldBeTrue();

        await _roles.DisableUser("jane");

        (await RolesOf("jane")).ShouldBe([Deactivated, "editor"]);
        // A new request (a new scoped provider) reads the shared cache, which the role change invalidated
        (await new UserAuthSnapshotProvider(_users, _snapshots).GetAsync(jane.Id)).IsDeactivated.ShouldBeTrue();
    }

    [Test]
    public async Task EnableUser_RemovesDeactivated_AndKeepsTheOtherRoles()
    {
        var jane = await CreateUser("jane", "editor", Deactivated);

        await _roles.EnableUser("jane");

        (await RolesOf("jane")).ShouldBe(["editor"]);
        (await new UserAuthSnapshotProvider(_users, _snapshots).GetAsync(jane.Id)).IsActive.ShouldBeTrue();
    }

    [Test]
    public async Task DisableAndEnable_AreIdempotent()
    {
        await CreateUser("jane");

        await _roles.DisableUser("jane");
        await _roles.DisableUser("jane");
        (await RolesOf("jane")).ShouldBe([Deactivated]);

        await _roles.EnableUser("jane");
        await _roles.EnableUser("jane");
        (await RolesOf("jane")).ShouldBeEmpty();
    }

    [Test]
    public async Task DisableAndEnable_AnUnknownUser_AreNotFound()
    {
        (await Should.ThrowAsync<NotFoundException>(() => _roles.DisableUser("nobody"))).StatusCode.ShouldBe(404);
        (await Should.ThrowAsync<NotFoundException>(() => _roles.EnableUser("nobody"))).StatusCode.ShouldBe(404);
    }

    [Test]
    public async Task DisableUser_YourOwnAccount_Is400()
    {
        await CreateUser("jane", Admin);
        CallerIs("jane");

        var ex = await Should.ThrowAsync<BadRequestException>(() => _roles.DisableUser("jane"));

        ex.Message.ShouldContain("your own account");
        (await RolesOf("jane")).ShouldBe([Admin]);
    }

    [Test]
    public async Task DisableUser_TheLastActiveAdmin_Is400()
    {
        // A caller whose token still says admin, though the role was taken away from it
        await CreateUser("operator");
        await CreateUser("other", Admin, Deactivated);
        CallerIs("operator");

        var ex = await Should.ThrowAsync<BadRequestException>(() => _roles.DisableUser("boss"));

        ex.Message.ShouldContain("last active administrator");
        (await RolesOf("boss")).ShouldBe([Admin]);
    }

    [Test]
    public async Task DisableUser_AnAdmin_WithAnotherActiveAdmin_IsDisabled()
    {
        await CreateUser("jane", Admin);

        await _roles.DisableUser("jane");

        (await RolesOf("jane")).ShouldBe([Admin, Deactivated]);
    }

    [Test]
    public async Task DisableUser_ForAnExternalProvider_DisablesLocally_AndCreatesTheRoleWhenMissing()
    {
        _login.Setup(l => l.Capabilities).Returns(AuthProviderCapabilities.External);
        await CreateUser("ldapuser", "editor");
        (await _roleManager.DeleteAsync((await _roleManager.FindByNameAsync(Deactivated))!)).Succeeded.ShouldBeTrue();

        await _roles.DisableUser("ldapuser");

        (await _roleManager.RoleExistsAsync(Deactivated)).ShouldBeTrue();
        (await RolesOf("ldapuser")).ShouldBe([Deactivated, "editor"]);

        await _roles.EnableUser("ldapuser");
        (await RolesOf("ldapuser")).ShouldBe(["editor"]);
    }

    [Test]
    public async Task DisableUser_WhenTheOtherAdminIsDisabledMeanwhile_TakesItBack_Is400()
    {
        // Two administrators disabling each other at once: both pass the first check
        await CreateUser("jane", Admin);
        await CreateUser("operator");
        CallerIs("operator");
        _users.AfterAddToRole = async (user, role) =>
        {
            if (user.UserName == "boss" && role == Deactivated)
                (await _users.AddToRoleAsync((await _users.FindByNameAsync("jane"))!, Deactivated)).Succeeded
                    .ShouldBeTrue();
        };

        var ex = await Should.ThrowAsync<BadRequestException>(() => _roles.DisableUser("boss"));

        ex.Message.ShouldContain("last active administrator");
        (await RolesOf("boss")).ShouldBe([Admin]);
        (await RolesOf("jane")).ShouldBe([Admin, Deactivated]);
    }

    #endregion

    #region Other role lists (D-043: only disable adds 'deactivated')

    [Test]
    public async Task CreateUser_WithDeactivated_Is400_AndCreatesNobody()
    {
        var ex = await Should.ThrowAsync<BadRequestException>(() =>
            _accounts.CreateUser("newbie", null, "Password1234", ["editor", Deactivated]));

        ex.Message.ShouldBe(SystemRoles.DeactivatedInRoleListMessage);
        (await _users.FindByNameAsync("newbie")).ShouldBeNull();
    }

    [Test]
    public async Task SetUserMeta_WithDeactivatedInItsRoles_Is400_AndChangesNothing()
    {
        await CreateUser("jane", "editor");

        var ex = await Should.ThrowAsync<BadRequestException>(() => _accounts.SetUserMeta("jane",
            new Dictionary<string, object> { ["roles"] = new List<object> { "editor", Deactivated } }));

        ex.Message.ShouldBe(SystemRoles.DeactivatedInRoleListMessage);
        (await RolesOf("jane")).ShouldBe(["editor"]);
    }

    #region SetUserMeta merges (FR-ADM-16)

    private async Task<Dictionary<string, object>> MetadataOf(string userName) =>
        (await _users.FindByNameAsync(userName))!.Metadata;

    [Test]
    public async Task SetUserMeta_MergesTheGivenKeys_AndKeepsTheOthers()
    {
        var jane = await CreateUser("jane", "editor");
        jane.Metadata = new Dictionary<string, object> { ["team"] = "north", ["maxStorageMB"] = 10L };
        (await _users.UpdateAsync(jane)).Succeeded.ShouldBeTrue();

        await _accounts.SetUserMeta("jane", new Dictionary<string, object> { ["maxStorageMB"] = Json("40") });

        var meta = await MetadataOf("jane");
        meta.Keys.OrderBy(k => k).ShouldBe(["maxStorageMB", "team"]);
        meta["team"].ShouldBe("north");
        ((JsonElement)meta["maxStorageMB"]).GetInt64().ShouldBe(40L);
    }

    [Test]
    public async Task SetUserMeta_ANullValueRemovesItsKey_AndNoOtherKey()
    {
        var jane = await CreateUser("jane");
        jane.Metadata = new Dictionary<string, object> { ["team"] = "north", ["maxStorageMB"] = 10L, ["other"] = true };
        (await _users.UpdateAsync(jane)).Succeeded.ShouldBeTrue();

        await _accounts.SetUserMeta("jane", new Dictionary<string, object> { ["maxStorageMB"] = Json("null") });
        (await MetadataOf("jane")).Keys.OrderBy(k => k).ShouldBe(["other", "team"]);

        await _accounts.SetUserMeta("jane", new Dictionary<string, object> { ["team"] = null! });
        (await MetadataOf("jane")).Keys.ShouldBe(["other"]);
    }

    [Test]
    public async Task SetUserMeta_WithoutRoles_DoesNotBringBackTheRolesStoredWithTheMetadata()
    {
        // The user was given 'editor' through Edit User; the 'roles' key stored with the metadata is older
        var jane = await CreateUser("jane", "editor");
        jane.Metadata = new Dictionary<string, object> { ["roles"] = new List<object> { "viewer" } };
        (await _users.UpdateAsync(jane)).Succeeded.ShouldBeTrue();

        await _accounts.SetUserMeta("jane", new Dictionary<string, object> { ["maxStorageMB"] = Json("40") });

        (await RolesOf("jane")).ShouldBe(["editor"]);
        (await MetadataOf("jane")).ContainsKey("roles").ShouldBeTrue();
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    #endregion

    [Test]
    public async Task ProviderSync_NeitherAddsNorRemovesDeactivated()
    {
        var active = await CreateUser("ldapactive", "editor");
        active.Metadata = new Dictionary<string, object> { ["roles"] = new List<object> { "viewer", Deactivated } };
        await _provisioning.SyncRolesAsync(active);
        (await RolesOf("ldapactive")).ShouldBe(["viewer"]);

        var disabled = await CreateUser("ldapdisabled", "editor", Deactivated);
        disabled.Metadata = new Dictionary<string, object> { ["roles"] = new List<object> { "viewer" } };
        await _provisioning.SyncRolesAsync(disabled);
        (await RolesOf("ldapdisabled")).ShouldBe([Deactivated, "viewer"]);
    }

    #endregion

    /// <summary>Runs <see cref="AfterAddToRole"/> once after a role is added: what another request does meanwhile.</summary>
    private sealed class HookedUserManager(IUserStore<User> store, Microsoft.Extensions.Options.IOptions<IdentityOptions> options,
        IPasswordHasher<User> hasher, IEnumerable<IUserValidator<User>> userValidators,
        IEnumerable<IPasswordValidator<User>> passwordValidators, ILookupNormalizer normalizer, IdentityErrorDescriber errors,
        IServiceProvider services, Microsoft.Extensions.Logging.ILogger<UserManager<User>> logger,
        UserAuthSnapshotCache snapshots)
        : RegistryUserManager(store, options, hasher, userValidators, passwordValidators, normalizer, errors, services,
            logger, snapshots)
    {
        public Func<User, string, Task>? AfterAddToRole { get; set; }

        public override async Task<IdentityResult> AddToRoleAsync(User user, string role)
        {
            var result = await base.AddToRoleAsync(user, role);
            var hook = AfterAddToRole;
            AfterAddToRole = null;
            if (hook != null)
                await hook(user, role);
            return result;
        }
    }
}
