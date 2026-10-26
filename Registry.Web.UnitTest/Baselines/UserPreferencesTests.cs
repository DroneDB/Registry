#nullable enable
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports.Json;
using Registry.Test.Common;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Users;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// <see cref="UserPreferences"/>, the signed-in user's Hub preferences, on a real SQLite <see cref="ApplicationDbContext"/> with
/// the real <see cref="UserManager{TUser}"/> (a fresh context reads what a request saved):
/// <list type="bullet">
/// <item>values: <c>{}</c> for a new user, a round trip of every JSON type, sections that merge, <c>null</c> that deletes one;</item>
/// <item>isolation: a <c>Patch</c> writes the <c>Preferences</c> column and nothing else (the <c>UPDATE</c> is captured), so a
/// "roles" section is only a section, and a login that replaces <c>Metadata</c> or an admin metadata replace keeps the
/// preferences;</item>
/// <item>limits: sections, names, depth, size and non-finite numbers are refused with a 400 and write nothing;</item>
/// <item>a row changed between the load and the save of the <c>Patch</c> is a 409, and nothing is written.</item>
/// </list>
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UserPreferencesTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class UserPreferencesTests
{
    private const string UserName = "userpreferences-user";
    private const string OtherUserName = "userpreferences-other";
    private const string EditorRole = "editor";

    private TestDb<ApplicationDbContext> _db = null!;
    private UpdateCapture _updates = null!;

    [SetUp]
    public void SetUp()
    {
        _updates = new UpdateCapture();
        _db = TestDb.Create<ApplicationDbContext>(options => options.AddInterceptors(_updates));
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    // ---- values ----

    [Test]
    public async Task Get_ANewUser_IsEmpty()
    {
        var userId = await CreateUser();
        using var request = Open(userId);

        (await request.Service.Get()).ShouldBeEmpty();
    }

    [Test]
    public async Task Patch_StoresEveryJsonType_AndAFreshContextReadsTheSameValues()
    {
        var userId = await CreateUser();
        using (var request = Open(userId))
        {
            var saved = await request.Service.Patch(Body("""
                {"map":{"zoom":12.5,"count":3,"big":9007199254740993,"whole":2.0,"neg":-7,"yes":true,"no":false,
                        "none":null,"center":[11.25,45],"nested":{"a":{"b":[1,"x",{"c":false}]}},
                        "name":"Zoë 日本語 😀 <b>&\"'</b>","empty":"","list":[],"obj":{}},
                 "units":{"measurement":"metric"}}
                """), default);

            saved.Keys.ShouldBe(["map", "units"], ignoreOrder: true);
        }

        using var fresh = Open(userId);
        var preferences = await fresh.Service.Get();

        Describe(preferences["units"]).ShouldBe("""{"measurement"=s:"metric"}""");
        var map = preferences["map"].ShouldBeOfType<Dictionary<string, object>>();
        map["zoom"].ShouldBe(12.5);
        map["count"].ShouldBeOfType<long>().ShouldBe(3L);
        map["big"].ShouldBeOfType<long>().ShouldBe(9007199254740993L, "a whole number keeps its 64-bit value");
        map["neg"].ShouldBe(-7L);
        map["whole"].ShouldBeOfType<double>().ShouldBe(2.0, "a number written with a fraction stays a double");
        map["yes"].ShouldBe(true);
        map["no"].ShouldBe(false);
        map.ShouldContainKey("none");
        map["none"].ShouldBeNull();
        Describe(map["center"]).ShouldBe("[d:" + BitConverter.DoubleToInt64Bits(11.25) + ",l:45]");
        Describe(map["nested"]).ShouldBe("""{"a"={"b"=[l:1,s:"x",{"c"=b:false}]}}""");
        map["name"].ShouldBe("Zoë 日本語 😀 <b>&\"'</b>");
        map["empty"].ShouldBe("");
        map["list"].ShouldBeOfType<List<object>>().ShouldBeEmpty();
        map["obj"].ShouldBeOfType<Dictionary<string, object>>().ShouldBeEmpty();
    }

    [Test]
    public async Task Patch_ANumberBeyondTheLongRange_IsStoredAsAFiniteDouble()
    {
        var userId = await CreateUser();
        using (var request = Open(userId))
            await request.Service.Patch(Body("""{"map":{"huge":9223372036854775808,"tiny":1e-300,"large":1e300}}"""), default);

        using var fresh = Open(userId);
        var map = (await fresh.Service.Get())["map"].ShouldBeOfType<Dictionary<string, object>>();
        map["huge"].ShouldBe(9223372036854775808d);
        map["tiny"].ShouldBe(1e-300);
        map["large"].ShouldBe(1e300);
    }

    [Test]
    public async Task Patch_TwoSections_MergeWithoutEitherReplacingTheOther()
    {
        var userId = await CreateUser();
        using (var request = Open(userId))
            await request.Service.Patch(Body("""{"units":{"measurement":"metric"}}"""), default);
        using (var request = Open(userId))
        {
            var after = await request.Service.Patch(Body("""{"map":{"zoom":3}}"""), default);

            after.Keys.ShouldBe(["units", "map"], ignoreOrder: true);
        }

        using var fresh = Open(userId);
        var preferences = await fresh.Service.Get();
        preferences.Keys.ShouldBe(["units", "map"], ignoreOrder: true);
        Describe(preferences["units"]).ShouldBe("""{"measurement"=s:"metric"}""");
        Describe(preferences["map"]).ShouldBe("""{"zoom"=l:3}""");
    }

    [Test]
    public async Task Patch_ASectionAgain_ReplacesThatSectionWhole_AndKeepsTheOthers()
    {
        var userId = await CreateUser();
        using (var request = Open(userId))
            await request.Service.Patch(Body("""{"map":{"zoom":3,"style":"sat"},"units":{"measurement":"metric"}}"""), default);
        using (var request = Open(userId))
            await request.Service.Patch(Body("""{"map":{"zoom":9}}"""), default);

        using var fresh = Open(userId);
        var preferences = await fresh.Service.Get();
        Describe(preferences["map"]).ShouldBe("""{"zoom"=l:9}""", "the section is the unit of replacement: no deep merge");
        preferences.ShouldContainKey("units");
    }

    [Test]
    public async Task Patch_NullDeletesThatSectionOnly()
    {
        var userId = await CreateUser();
        using (var request = Open(userId))
            await request.Service.Patch(Body("""{"map":{"zoom":3},"units":{"measurement":"metric"}}"""), default);
        using (var request = Open(userId))
        {
            var after = await request.Service.Patch(Body("""{"map":null}"""), default);

            after.Keys.ShouldBe(["units"]);
        }

        using var fresh = Open(userId);
        (await fresh.Service.Get()).Keys.ShouldBe(["units"]);
    }

    [Test]
    public async Task Patch_NullForASectionThatIsNotThere_IsAccepted_AndStoresNothingNew()
    {
        var userId = await CreateUser();
        using (var request = Open(userId))
            (await request.Service.Patch(Body("""{"map":null}"""), default)).ShouldBeEmpty();

        using var fresh = Open(userId);
        (await fresh.Service.Get()).ShouldBeEmpty();
    }

    [Test]
    public async Task Patch_OneUser_DoesNotChangeAnother()
    {
        var userId = await CreateUser();
        var otherId = await CreateUser(OtherUserName);
        using (var request = Open(userId))
            await request.Service.Patch(Body("""{"map":{"zoom":3}}"""), default);

        using var other = Open(otherId);
        (await other.Service.Get()).ShouldBeEmpty();
    }

    // ---- only the Preferences column is written ----

    /// <remarks>
    /// Fails if the service marked the entity as modified (<c>Entry(user).State = Modified</c>, <c>Update(user)</c>, or
    /// <c>UserManager.UpdateAsync</c>): each puts every column in the <c>SET</c> list, and <c>UpdateAsync</c> also rotates
    /// the <c>ConcurrencyStamp</c>.
    /// </remarks>
    [Test]
    public async Task Patch_SendsOneUpdateThatSetsThePreferencesColumnOnly()
    {
        var userId = await CreateUser(metadata: new Dictionary<string, object>
            { ["maxStorage"] = 5L, ["roles"] = new List<object> { EditorRole } });
        var before = await Snapshot(userId);
        _updates.Clear();

        using (var request = Open(userId))
            await request.Service.Patch(Body("""{"map":{"zoom":3}}"""), default);

        var updates = _updates.UserUpdates();
        updates.Count.ShouldBe(1);
        SetColumns(updates[0]).ShouldBe(["Preferences"], "SQL: " + updates[0]);

        var after = await Snapshot(userId);
        after.ConcurrencyStamp.ShouldBe(before.ConcurrencyStamp, "a preferences write never rotates the stamp");
        after.SecurityStamp.ShouldBe(before.SecurityStamp);
        after.MetadataText.ShouldBe(before.MetadataText);
        after.Roles.ShouldBe(before.Roles);
        after.Email.ShouldBe(before.Email);
        after.PasswordHash.ShouldBe(before.PasswordHash);
        after.PreferencesText.ShouldBe("""{"map":{"zoom":3}}""");
    }

    /// <summary>Guards the capture: the write the service must not make (every column) shows up as a long <c>SET</c> list.</summary>
    [Test]
    public async Task TheCapture_SeesTheOtherColumnsWhenTheWholeEntityIsMarkedModified()
    {
        var userId = await CreateUser();
        using var context = _db.NewContext();
        var user = await context.Users.SingleAsync(u => u.Id == userId);
        _updates.Clear();

        context.Entry(user).State = EntityState.Modified;
        await context.SaveChangesAsync();

        var columns = SetColumns(_updates.UserUpdates().ShouldHaveSingleItem());
        columns.ShouldContain("Metadata");
        columns.ShouldContain("Preferences");
        columns.ShouldContain("Email");
    }

    [Test]
    public async Task Patch_ASecondPatchOnTheSameRequest_StillSetsThePreferencesColumnOnly()
    {
        var userId = await CreateUser();
        using var request = Open(userId);
        await request.Service.Patch(Body("""{"map":{"zoom":3}}"""), default);
        _updates.Clear();

        await request.Service.Patch(Body("""{"units":{"measurement":"metric"}}"""), default);

        var updates = _updates.UserUpdates();
        updates.Count.ShouldBe(1);
        SetColumns(updates[0]).ShouldBe(["Preferences"]);
    }

    [Test]
    public async Task Patch_ThatChangesNothing_WritesNothing()
    {
        var userId = await CreateUser();
        using (var request = Open(userId))
            await request.Service.Patch(Body("""{"map":{"zoom":3},"units":{"measurement":"metric"}}"""), default);
        _updates.Clear();

        using (var request = Open(userId))
            await request.Service.Patch(Body("""{"map":{"zoom":3}}"""), default);

        _updates.UserUpdates().ShouldBeEmpty("the same value is not a change (by-content comparer)");
    }

    // ---- role isolation ----

    [Test]
    public async Task Patch_ARolesSection_IsOnlyASection_AndGrantsNoRole()
    {
        await EnsureRole(ApplicationDbContext.AdminRoleName);
        var userId = await CreateUser(roles: [EditorRole]);
        var before = await Snapshot(userId);

        using (var request = Open(userId))
        {
            var saved = await request.Service.Patch(Body("""{"roles":["admin"]}"""), default);

            saved.ShouldContainKey("roles");
        }

        var after = await Snapshot(userId);
        after.Roles.ShouldBe([EditorRole], "no Identity role changed");
        after.Roles.ShouldNotContain(ApplicationDbContext.AdminRoleName);
        after.MetadataText.ShouldBe(before.MetadataText);
        after.MetadataText.ShouldNotContain("admin");
        using var fresh = Open(userId);
        var roles = (await fresh.Service.Get())["roles"].ShouldBeOfType<List<object>>();
        roles.ShouldBe(["admin"], "the roles section reads back as it was stored");
    }

    [Test]
    public async Task Patch_ARolesSection_DoesNotRunTheRoleSync()
    {
        // Metadata["roles"] drives SyncRolesAsync; if the preferences path ever fed it, the editor role would be removed
        var userId = await CreateUser(roles: [EditorRole], metadata: new Dictionary<string, object>
            { ["roles"] = new List<object> { EditorRole } });
        _updates.Clear();

        using (var request = Open(userId))
            await request.Service.Patch(Body("""{"roles":[]}"""), default);

        (await Snapshot(userId)).Roles.ShouldBe([EditorRole]);
        using var check = _db.NewContext();
        (await check.UserRoles.CountAsync()).ShouldBe(1);
    }

    // ---- what else writes the user keeps the preferences ----

    [Test]
    public async Task ARelogin_ThatReplacesMetadata_KeepsEveryPreference()
    {
        var userId = await CreateUser();
        using (var request = Open(userId))
            await request.Service.Patch(Body("""{"map":{"zoom":3},"units":{"measurement":"imperial"},"roles":["x"]}"""), default);

        // The next login, in another request: an LDAP-style provider returns its own keys only
        using (var login = Open(userId))
        {
            var loaded = await login.Manager.FindByIdAsync(userId);
            await login.Provisioning.SyncFromProviderAsync(loaded!, new Dictionary<string, object>
            {
                ["email"] = "ldap@example.com",
                ["displayName"] = "Mario Rossi",
                ["roles"] = new List<object> { EditorRole },
                ["authProvider"] = "ldap"
            });
        }

        var after = await Snapshot(userId);
        after.MetadataText.ShouldContain("authProvider", Case.Sensitive, "the login did replace the metadata");
        using var fresh = Open(userId);
        var preferences = await fresh.Service.Get();
        preferences.Keys.ShouldBe(["map", "units", "roles"], ignoreOrder: true);
        Describe(preferences["map"]).ShouldBe("""{"zoom"=l:3}""");
        Describe(preferences["units"]).ShouldBe("""{"measurement"=s:"imperial"}""");
    }

    [Test]
    public async Task AReloginWithNoMetadata_KeepsEveryPreference()
    {
        var userId = await CreateUser(metadata: new Dictionary<string, object> { ["maxStorage"] = 5L });
        using (var request = Open(userId))
            await request.Service.Patch(Body("""{"map":{"zoom":3}}"""), default);

        using (var login = Open(userId))
            await login.Provisioning.SyncFromProviderAsync((await login.Manager.FindByIdAsync(userId))!, null!);

        using var fresh = Open(userId);
        (await fresh.Service.Get()).ShouldContainKey("map");
    }

    [Test]
    public async Task AdminMetadataReplace_KeepsEveryPreference()
    {
        var userId = await CreateUser();
        var adminId = await CreateUser("userpreferences-admin");
        using (var request = Open(userId))
            await request.Service.Patch(Body("""{"map":{"zoom":3},"units":{"measurement":"metric"}}"""), default);

        using (var admin = Open(adminId))
            await admin.Accounts.SetUserMeta(UserName,
                new Dictionary<string, object> { ["maxStorage"] = 9L, ["roles"] = new List<object> { EditorRole } });

        var after = await Snapshot(userId);
        after.MetadataText.ShouldContain("maxStorage", Case.Sensitive, "the admin replace did write the metadata");
        after.Roles.ShouldBe([EditorRole], "and its roles were synchronised");
        using var fresh = Open(userId);
        (await fresh.Service.Get()).Keys.ShouldBe(["map", "units"], ignoreOrder: true);
    }

    [Test]
    public async Task Patch_BeforeAndAfterAnAdminMetadataReplace_KeepsBothSections()
    {
        var userId = await CreateUser();
        var adminId = await CreateUser("userpreferences-admin");
        using (var request = Open(userId))
            await request.Service.Patch(Body("""{"map":{"zoom":3}}"""), default);
        using (var admin = Open(adminId))
            await admin.Accounts.SetUserMeta(UserName, new Dictionary<string, object> { ["k"] = 1L });
        using (var request = Open(userId))
            await request.Service.Patch(Body("""{"units":{"measurement":"metric"}}"""), default);

        using var fresh = Open(userId);
        (await fresh.Service.Get()).Keys.ShouldBe(["map", "units"], ignoreOrder: true);
        (await Snapshot(userId)).MetadataText.ShouldBe("""{"k":1}""");
    }

    // ---- limits ----

    [Test]
    public async Task Patch_32Sections_AreAccepted_AndThe33rd_IsRefused()
    {
        var userId = await CreateUser();
        using (var request = Open(userId))
            (await request.Service.Patch(Sections(32), default)).Count.ShouldBe(32);

        await ShouldBeRefused(userId, Body("""{"extra":1}"""), "32", expectStored: Sections(32).Keys.ToArray());
    }

    [Test]
    public async Task Patch_33SectionsAtOnce_AreRefused_AndNothingIsWritten()
    {
        var userId = await CreateUser();

        await ShouldBeRefused(userId, Sections(33), "32");
    }

    [Test]
    public async Task Patch_ADeletionAndAnAddition_AtTheLimit_KeepTheCountAt32()
    {
        var userId = await CreateUser();
        using (var request = Open(userId))
            await request.Service.Patch(Sections(32), default);

        using (var request = Open(userId))
        {
            var patch = Body("""{"s0":null,"fresh":1}""");
            (await request.Service.Patch(patch, default)).Count.ShouldBe(32);
        }

        using var fresh = Open(userId);
        var keys = (await fresh.Service.Get()).Keys;
        keys.ShouldContain("fresh");
        keys.ShouldNotContain("s0");
    }

    [Test]
    public async Task Patch_ReplacingASectionAtTheLimit_IsNotANewSection()
    {
        var userId = await CreateUser();
        using (var request = Open(userId))
            await request.Service.Patch(Sections(32), default);

        using var again = Open(userId);
        (await again.Service.Patch(Body("""{"s0":{"changed":true}}"""), default)).Count.ShouldBe(32);
    }

    [TestCase("Map")]
    [TestCase("MAP")]
    [TestCase("a-b")]
    [TestCase("a_b")]
    [TestCase("a b")]
    [TestCase("a.b")]
    [TestCase("1map")]
    [TestCase("_map")]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("map ")]
    [TestCase("map\n")]
    [TestCase("map\r\n")]
    [TestCase("\nmap")]
    [TestCase("mäp")]
    [TestCase("日本")]
    [TestCase("a123456789a123456789a123456789abc")] // 33 characters
    public async Task Patch_ABadSectionName_IsRefused_AndNothingIsWritten(string name)
    {
        var userId = await CreateUser();
        var patch = new Dictionary<string, JsonElement> { [name] = Element("1") };

        await ShouldBeRefused(userId, patch, "section name");
    }

    [TestCase("a")]
    [TestCase("map")]
    [TestCase("mapSettings1")]
    [TestCase("aB9")]
    [TestCase("a123456789a123456789a123456789ab")] // 32 characters
    public async Task Patch_AValidSectionName_IsAccepted(string name)
    {
        name.Length.ShouldBeLessThanOrEqualTo(32);
        var userId = await CreateUser();
        using (var request = Open(userId))
            await request.Service.Patch(new Dictionary<string, JsonElement> { [name] = Element("1") }, default);

        using var fresh = Open(userId);
        (await fresh.Service.Get()).Keys.ShouldBe([name]);
    }

    [Test]
    public async Task Patch_AnInvalidNameNextToValidOnes_RefusesTheWholePatch()
    {
        var userId = await CreateUser();
        var patch = Body("""{"map":{"zoom":3},"Bad":1,"units":{"m":"x"}}""");

        await ShouldBeRefused(userId, patch, "section name");
    }

    [TestCase(1)]
    [TestCase(4)]
    [TestCase(8)]
    public async Task Patch_ASectionNestedUpToTheLimit_IsAccepted(int levels)
    {
        var userId = await CreateUser();
        using (var request = Open(userId))
            await request.Service.Patch(Body($$"""{"map":{{NestedObjects(levels)}}}"""), default);

        using var fresh = Open(userId);
        (await fresh.Service.Get()).ShouldContainKey("map");
    }

    [TestCase(9)]
    [TestCase(20)]
    public async Task Patch_ASectionNestedDeeperThanTheLimit_IsRefused_AndNothingIsWritten(int levels)
    {
        var userId = await CreateUser();

        await ShouldBeRefused(userId, Body($$"""{"map":{{NestedObjects(levels)}}}"""), "deeper than 8");
    }

    [Test]
    public async Task Patch_Depth_CountsArraysAsObjectsDo()
    {
        var userId = await CreateUser();
        using (var request = Open(userId))
            await request.Service.Patch(Body("""{"map":[[[[[[[[1]]]]]]]]}"""), default); // 8 levels

        await ShouldBeRefused(userId, Body("""{"map":[[[[[[[[[1]]]]]]]]]}"""), "deeper than 8", expectStored: ["map"]); // 9
        await ShouldBeRefused(userId, Body("""{"map":{"a":[{"b":[{"c":[{"d":[{"e":1}]}]}]}]}}"""), "deeper than 8",
            expectStored: ["map"]); // 9: o a o a o a o a o
    }

    [Test]
    public async Task Patch_AScalarSection_IsLevelZero()
    {
        var userId = await CreateUser();
        using (var request = Open(userId))
            await request.Service.Patch(Body("""{"a":1,"b":"text","c":true,"d":1.5}"""), default);

        using var fresh = Open(userId);
        (await fresh.Service.Get()).Count.ShouldBe(4);
    }

    [Test]
    public async Task Patch_AMergedValueOfExactlyTheLimit_IsAccepted_AndOneCharacterMore_IsRefused()
    {
        var userId = await CreateUser();
        // {"a":"<n>"} is 8 characters plus the text
        using (var request = Open(userId))
            await request.Service.Patch(Body($$"""{"a":"{{new string('x', UserPreferences.MaxTotalBytes - 8)}}"}"""), default);
        (await Snapshot(userId)).PreferencesText.Length.ShouldBe(UserPreferences.MaxTotalBytes);

        await ShouldBeRefused(userId, Body($$"""{"a":"{{new string('x', UserPreferences.MaxTotalBytes - 8 + 1)}}"}"""),
            "65536", expectStored: ["a"]);
    }

    [Test]
    public async Task Patch_SectionsThatTogetherExceedTheLimit_AreRefused_AndTheEarlierOnesStay()
    {
        var userId = await CreateUser();
        using (var request = Open(userId))
            await request.Service.Patch(Body($$"""{"first":"{{new string('x', 40_000)}}"}"""), default);

        await ShouldBeRefused(userId, Body($$"""{"second":"{{new string('y', 40_000)}}"}"""), "65536",
            expectStored: ["first"]);
    }

    [Test]
    public async Task Patch_ARefusedPatch_LeavesTheTrackedUserAsItWas()
    {
        var userId = await CreateUser();
        using var request = Open(userId);
        await request.Service.Patch(Body("""{"map":{"zoom":3}}"""), default);

        await Should.ThrowAsync<BadRequestException>(() => request.Service.Patch(Body("""{"Bad":1,"units":{"m":"x"}}"""), default));

        // The same request goes on: a refused patch left no half-applied state to save
        _updates.Clear();
        await request.Service.Patch(Body("""{"general":{"k":1}}"""), default);
        var stored = (await Snapshot(userId)).PreferencesText;
        stored.ShouldNotContain("units");
        stored.ShouldContain("general");
    }

    [TestCase("""{"a":1e400}""")]
    [TestCase("""{"a":-1e400}""")]
    [TestCase("""{"a":{"b":[1,{"c":1e999}]}}""")]
    [TestCase("""{"a":[1e309]}""")]
    public async Task Patch_ANumberThatIsNotFinite_IsRefused_AndNothingIsWritten(string json)
    {
        var userId = await CreateUser();

        await ShouldBeRefused(userId, Body(json), "outside the range");
    }

    [Test]
    public async Task Patch_ANull_IsRefused()
    {
        var userId = await CreateUser();
        using var request = Open(userId);

        await Should.ThrowAsync<BadRequestException>(() => request.Service.Patch(null, default));
    }

    // ---- a changed account row ----

    [Test]
    public async Task Patch_WhenTheAccountChangesBetweenTheLoadAndTheSave_IsAConflict_AndNothingIsWritten()
    {
        var userId = await CreateUser();
        var before = await Snapshot(userId);

        using (var request = Open(userId, afterLoad: async () =>
               {
                   // Another request (a role change, a password change, a login) rotates the stamp
                   using var other = Open(userId);
                   var user = (await other.Manager.FindByIdAsync(userId))!;
                   user.Email = "changed@example.com";
                   (await other.Manager.UpdateAsync(user)).Succeeded.ShouldBeTrue();
               }))
        {
            await Should.ThrowAsync<ConflictException>(() =>
                request.Service.Patch(Body("""{"map":{"zoom":3}}"""), default));
        }

        var after = await Snapshot(userId);
        after.PreferencesText.ShouldBe("{}", "nothing of the refused patch is stored");
        after.Email.ShouldBe("changed@example.com", "the other write stays");
        after.ConcurrencyStamp.ShouldNotBe(before.ConcurrencyStamp);
    }

    [Test]
    public async Task Patch_AfterAConflict_LoadingAgainSucceeds()
    {
        var userId = await CreateUser();
        using (var request = Open(userId, afterLoad: () => RotateStamp(userId)))
            await Should.ThrowAsync<ConflictException>(() => request.Service.Patch(Body("""{"map":{"zoom":3}}"""), default));

        using (var request = Open(userId))
            await request.Service.Patch(Body("""{"map":{"zoom":3}}"""), default);

        using var fresh = Open(userId);
        (await fresh.Service.Get()).ShouldContainKey("map");
    }

    [Test]
    public async Task Patch_DoesNotMakeAnAccountWriteOfAnotherRequestFail()
    {
        var userId = await CreateUser();
        using var account = Open(userId);
        var loaded = (await account.Manager.FindByIdAsync(userId))!;

        using (var request = Open(userId))
            await request.Service.Patch(Body("""{"map":{"zoom":3}}"""), default);

        loaded.Email = "after-a-patch@example.com";
        (await account.Manager.UpdateAsync(loaded)).Succeeded.ShouldBeTrue("the patch did not rotate the stamp");
    }

    // ---- no current user ----

    [Test]
    public async Task NoCurrentUser_IsUnauthorized_ForGetAndPatch()
    {
        using var request = Open(userId: null);

        await Should.ThrowAsync<UnauthorizedException>(() => request.Service.Get());
        await Should.ThrowAsync<UnauthorizedException>(() => request.Service.Patch(Body("""{"map":{"zoom":3}}"""), default));
    }

    [Test]
    public async Task NoCurrentUser_ABadBodyIsStillRefused_AndNothingIsWritten()
    {
        var userId = await CreateUser();
        using var request = Open(userId: null);

        await Should.ThrowAsync<Exception>(() => request.Service.Patch(Body("""{"Bad":1}"""), default));

        (await Snapshot(userId)).PreferencesText.ShouldBe("{}");
        _updates.UserUpdates().ShouldBeEmpty();
    }

    // ---- helpers ----

    private async Task ShouldBeRefused(string userId, Dictionary<string, JsonElement> patch, string messagePart,
        string[]? expectStored = null)
    {
        var before = await Snapshot(userId);
        _updates.Clear();

        using (var request = Open(userId))
        {
            var ex = await Should.ThrowAsync<BadRequestException>(() => request.Service.Patch(patch, default));
            ex.Message.ShouldContain(messagePart);
        }

        _updates.UserUpdates().ShouldBeEmpty("a refused patch sends no UPDATE");
        var after = await Snapshot(userId);
        after.PreferencesText.ShouldBe(before.PreferencesText);
        after.ConcurrencyStamp.ShouldBe(before.ConcurrencyStamp);
        using var fresh = Open(userId);
        (await fresh.Service.Get()).Keys.ShouldBe(expectStored ?? [], ignoreOrder: true);
    }

    private static string NestedObjects(int levels)
    {
        var text = "1";
        for (var i = 0; i < levels; i++)
            text = "{\"a\":" + text + "}";
        return text;
    }

    private static Dictionary<string, JsonElement> Sections(int count) =>
        Enumerable.Range(0, count).ToDictionary(i => "s" + i, _ => Element("""{"v":1}"""));

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>The body the controller binds: <c>Dictionary&lt;string, JsonElement&gt;</c> from the JSON text.</summary>
    private static Dictionary<string, JsonElement> Body(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    /// <summary>A text form of a plain value that keeps the CLR type: <c>s:</c> string, <c>l:</c> long, <c>d:</c> double.</summary>
    private static string Describe(object? value) => value switch
    {
        null => "null",
        string s => "s:" + JsonSerializer.Serialize(s),
        bool b => "b:" + (b ? "true" : "false"),
        long l => "l:" + l,
        double d => "d:" + BitConverter.DoubleToInt64Bits(d),
        IDictionary<string, object?> map =>
            "{" + string.Join(",", map.Select(p => JsonSerializer.Serialize(p.Key) + "=" + Describe(p.Value))) + "}",
        IEnumerable<object?> list => "[" + string.Join(",", list.Select(Describe)) + "]",
        _ => throw new AssertionException($"{value.GetType()} is not a plain value")
    };

    private static string[] SetColumns(string sql)
    {
        var match = Regex.Match(sql, @"\bSET\s+(?<set>.*?)\s+WHERE\b", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        match.Success.ShouldBeTrue("an UPDATE ... SET ... WHERE: " + sql);
        return Regex.Matches(match.Groups["set"].Value, @"[""`\[]?(?<column>\w+)[""`\]]?\s*=\s*@")
            .Select(m => m.Groups["column"].Value).ToArray();
    }

    private async Task<string> CreateUser(string userName = UserName, string[]? roles = null,
        Dictionary<string, object>? metadata = null)
    {
        using var context = _db.NewContext();
        foreach (var role in (roles ?? []).Append(EditorRole).Distinct())
            if (!await context.Roles.AnyAsync(r => r.Name == role))
                context.Roles.Add(new IdentityRole(role) { NormalizedName = role.ToUpperInvariant() });
        await context.SaveChangesAsync();

        var manager = NewManager(context);
        var user = new User { UserName = userName, Email = userName + "@example.com", Metadata = metadata! };
        (await manager.CreateAsync(user, "Password1234!")).Succeeded.ShouldBeTrue();
        foreach (var role in roles ?? [])
            (await manager.AddToRoleAsync(user, role)).Succeeded.ShouldBeTrue();
        return user.Id;
    }

    private async Task EnsureRole(string role)
    {
        using var context = _db.NewContext();
        if (!await context.Roles.AnyAsync(r => r.Name == role))
            context.Roles.Add(new IdentityRole(role) { NormalizedName = role.ToUpperInvariant() });
        await context.SaveChangesAsync();
    }

    private async Task RotateStamp(string userId)
    {
        using var other = Open(userId);
        var user = (await other.Manager.FindByIdAsync(userId))!;
        (await other.Manager.UpdateAsync(user)).Succeeded.ShouldBeTrue();
    }

    /// <summary>One request: its own context, <see cref="UserManager{TUser}"/> and services, as the scoped container gives.</summary>
    private Request Open(string? userId, Func<Task>? afterLoad = null)
    {
        var context = _db.NewContext();
        var manager = NewManager(context);
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.GetCurrentUser()).Returns(async () =>
        {
            if (userId == null)
                return null!;
            var user = await manager.FindByIdAsync(userId);
            if (afterLoad != null)
                await afterLoad();
            return user!;
        });

        var provisioning = new UserProvisioning(manager, NewRoleManager(context), context,
            new Mock<IPasswordPolicyValidator>().Object, TestSettings.Options<AccountOptions>(TestSettings.Mutable()),
            new Mock<IOrganizationsManager>().Object, NullLogger<UserProvisioning>.Instance);
        var login = new Mock<ILoginManager>();
        login.Setup(l => l.Capabilities).Returns(AuthProviderCapabilities.Local);
        var accounts = new UserAccounts(login.Object, manager, provisioning, auth.Object, context, null!,
            NullLogger<UserAccounts>.Instance);

        return new Request(new UserPreferences(auth.Object, context), manager, provisioning, accounts);
    }

    private sealed record Request(UserPreferences Service, UserManager<User> Manager, UserProvisioning Provisioning,
        UserAccounts Accounts) : IDisposable
    {
        // The contexts belong to the TestDb handle, which drops them
        public void Dispose()
        {
        }
    }

    private static UserManager<User> NewManager(ApplicationDbContext context) =>
        new(new UserStore<User, IdentityRole, ApplicationDbContext>(context), Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
            new PasswordHasher<User>(), [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(),
            null!, NullLogger<UserManager<User>>.Instance);

    private static RoleManager<IdentityRole> NewRoleManager(ApplicationDbContext context) =>
        new(new RoleStore<IdentityRole>(context), [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(),
            NullLogger<RoleManager<IdentityRole>>.Instance);

    private sealed record Stored(string PreferencesText, string MetadataText, string ConcurrencyStamp, string SecurityStamp,
        string Email, string PasswordHash, string[] Roles);

    /// <summary>The row as stored (raw columns, no tracking), read through a context nobody else uses.</summary>
    private async Task<Stored> Snapshot(string userId)
    {
        using var context = _db.NewContext();
        var row = await context.Database.SqlQuery<RawRow>($"""
            SELECT Preferences AS PreferencesText, Metadata AS MetadataText, ConcurrencyStamp, SecurityStamp, Email, PasswordHash
            FROM AspNetUsers WHERE Id = {userId}
            """).SingleAsync();
        var roles = await context.Database.SqlQuery<string>($"""
            SELECT r.Name AS Value FROM AspNetUserRoles ur JOIN AspNetRoles r ON r.Id = ur.RoleId
            WHERE ur.UserId = {userId} ORDER BY r.Name
            """).ToListAsync();
        return new Stored(row.PreferencesText, row.MetadataText, row.ConcurrencyStamp, row.SecurityStamp, row.Email,
            row.PasswordHash, roles.ToArray());
    }

    // ReSharper disable once ClassNeverInstantiated.Local
    private sealed record RawRow(string PreferencesText, string MetadataText, string ConcurrencyStamp, string SecurityStamp,
        string Email, string PasswordHash);

    /// <summary>Records the SQL of every command that updates <c>AspNetUsers</c>.</summary>
    private sealed class UpdateCapture : DbCommandInterceptor
    {
        private readonly List<string> _commands = [];

        public void Clear()
        {
            lock (_commands)
                _commands.Clear();
        }

        public List<string> UserUpdates()
        {
            lock (_commands)
                return _commands.Where(c => Regex.IsMatch(c, @"^\s*UPDATE\s+[""`\[]?AspNetUsers\b",
                    RegexOptions.IgnoreCase)).ToList();
        }

        private void Record(DbCommand command)
        {
            lock (_commands)
                _commands.Add(command.CommandText);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }
    }
}
