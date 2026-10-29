using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Users;
using NUnit.Framework;
using Registry.Common;
using Registry.Test.Common;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// <c>User.Metadata</c>: the column moves from Json.NET to System.Text.Json without a migration, and its value
/// comparer compares values.
/// <list type="bullet">
/// <item>Every row an older Registry stored was written by Json.NET (<see cref="LegacyUserMetadataJson"/> is that code,
/// verbatim): the new reader gives the same values, with the same CLR types (<c>long</c>, <c>double</c>, nested
/// <c>List&lt;object&gt;</c> and <c>Dictionary&lt;string, object&gt;</c>), and the new writer writes the same text, so an
/// older Registry reads what the new one wrote (a downgrade).</item>
/// <item>The comparer EF keeps the snapshot with (and <c>UserProvisioning</c> asks before saving) was the reference hash and
/// <c>IsSameAs</c>, which compares a list value by reference: a user with <c>roles</c> was "modified" on every login.</item>
/// </list>
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UserMetadataTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class UserMetadataTests
{
    private static Dictionary<string, object> Dict(params (string Key, object? Value)[] items) =>
        items.ToDictionary(i => i.Key, i => i.Value!);

    private static readonly (string Name, Dictionary<string, object> Metadata)[] Corpus =
    [
        ("empty", Dict()),
        ("a login provider", Dict(("displayName", "Mario Rossi"), ("mail", "m.rossi@example.com"),
            ("roles", new List<object> { "admin", "editors" }), ("maxStorage", 1073741824L))),
        ("non-ASCII and markup", Dict(("name", "Zoë <b>&'\"é日本</b>"), ("accents", "àèìòù ß ñ"))),
        ("an astral-plane character", Dict(("emoji", "x\U0001F600y"))),
        ("numbers", Dict(("int", 1L), ("negative", -42L), ("max", long.MaxValue), ("min", long.MinValue),
            ("fraction", 1.5), ("whole", 2.0), ("small", 1e-7), ("large", 1e300), ("zero", 0L), ("negzero", -0.0))),
        ("booleans and null", Dict(("yes", true), ("no", false), ("nothing", null))),
        ("nested", Dict(("a", Dict(("b", new List<object?> { 1L, Dict(("c", "d")), null, new List<object> { true } }))))),
        ("date-looking strings stay strings", Dict(("created", "2020-01-02T03:04:05Z"), ("day", "2020-01-02"),
            ("offset", "2020-01-02T03:04:05+02:00"))),
        ("escapes", Dict(("text", "line1\nline2\t\"quoted\" \\ back \u0001 \b\f\r"))),
        ("control characters with a hex letter", Dict(("text", "a\u001fb\u000bc"))),
        ("keys that need care", Dict(("", 1L), ("with space", 2L), ("ключ", 3L), ("a\"b", 4L))),
        ("an empty list and an empty object", Dict(("l", new List<object>()), ("o", new Dictionary<string, object>())))
    ];

    private static IEnumerable<TestCaseData> CorpusCases() =>
        Corpus.Select(c => new TestCaseData(c.Metadata).SetName(c.Name));

    /// <summary>A text form of a value that keeps what the type says: <c>s:</c> string, <c>l:</c> long, <c>d:</c> double.</summary>
    private static string Describe(object? value) => value switch
    {
        null => "null",
        string s => "s:" + JsonSerializer.Serialize(s),
        bool b => "b:" + (b ? "true" : "false"),
        long l => "l:" + l,
        double d => "d:" + BitConverter.DoubleToInt64Bits(d),
        IDictionary<string, object?> map =>
            "{" + string.Join(",", map.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => JsonSerializer.Serialize(p.Key) + "=" + Describe(p.Value))) + "}",
        IEnumerable<object?> list => "[" + string.Join(",", list.Select(Describe)) + "]",
        _ => throw new AssertionException($"{value.GetType()} is not a plain metadata value")
    };

    [TestCaseSource(nameof(CorpusCases))]
    public void TheTextTheOldConverterWrote_ReadsToTheSameValues_WithTheSameTypes(Dictionary<string, object> metadata)
    {
        var stored = LegacyUserMetadataJson.Serialize(metadata);

        var read = UserMetadataJson.Deserialize(stored);

        read.ShouldNotBeNull();
        Describe(read).ShouldBe(Describe(LegacyUserMetadataJson.Deserialize(stored)));
        Describe(read).ShouldBe(Describe(metadata));
    }

    // Json.NET writes an astral-plane character as it is and a control character's hex digits in lower case; System.Text.Json
    // escapes the former as a surrogate pair and writes the latter in upper case. The text means the same (the
    // downgrade test reads it with Json.NET); everything else is byte for byte what Json.NET wrote
    private static readonly string[] SpelledDifferently = ["an astral-plane character", "control characters with a hex letter"];

    [TestCaseSource(nameof(CorpusCases))]
    public void TheNewConverterWritesTheTextTheOldOneWrote(Dictionary<string, object> metadata)
    {
        var written = UserMetadataJson.Serialize(metadata);
        var legacy = LegacyUserMetadataJson.Serialize(metadata);

        if (written == legacy)
            return;

        SpelledDifferently.ShouldContain(Corpus.Single(c => ReferenceEquals(c.Metadata, metadata)).Name,
            "only the spelling of an escape may differ");
        System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(written),
            System.Text.Json.Nodes.JsonNode.Parse(legacy)).ShouldBeTrue();
    }

    [TestCaseSource(nameof(CorpusCases))]
    public void TheTextTheNewConverterWrote_ReadsWithTheOldConverter(Dictionary<string, object> metadata)
    {
        var stored = UserMetadataJson.Serialize(metadata);

        Describe(LegacyUserMetadataJson.Deserialize(stored)).ShouldBe(Describe(metadata));
    }

    [Test]
    public void AJsonElementTheApiAssigned_IsWrittenAsTheJsonItHolds_LikeBefore()
    {
        using var document = JsonDocument.Parse("""{"roles":["a","b"],"quota":5,"ratio":0.25,"nested":{"x":null}}""");
        var metadata = Dict(("body", document.RootElement.Clone()), ("plain", "p"));

        var stored = UserMetadataJson.Serialize(metadata);

        stored.ShouldBe(LegacyUserMetadataJson.Serialize(metadata));
        stored.ShouldBe("""{"body":{"roles":["a","b"],"quota":5,"ratio":0.25,"nested":{"x":null}},"plain":"p"}""");
    }

    private static readonly string[] Names = ["a", "b"];

    [Test]
    public void AnythingElseThePlatformPutsInMetadata_IsStillWritten_LikeBefore()
    {
        var metadata = Dict(("count", 3), ("when", new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc)),
            ("id", Guid.Parse("0a223495-84a0-4c15-b425-c7ef88110e75")), ("names", Names),
            ("single", 1.5f), ("decimal", 2.50m));

        var stored = UserMetadataJson.Serialize(metadata);

        // The same JSON values (the read side only has the plain types)
        using var written = JsonDocument.Parse(stored);
        using var oracle = JsonDocument.Parse(LegacyUserMetadataJson.Serialize(metadata));
        written.RootElement.GetRawText().ShouldBe(oracle.RootElement.GetRawText());
    }

    [TestCase("null")]
    public void TheJsonLiteralNull_ReadsAsNull_AsBefore(string stored) =>
        UserMetadataJson.Deserialize(stored).ShouldBeNull();

    [TestCase("""{"a":1,"a":2}""")]
    [TestCase("""{"a": 1 ,"b" :[ 1 , 2 ]}""")]
    [TestCase("{\n  \"a\": \"x\"\n}")]
    public void TextOtherToolsWrote_Whitespace_IsRead(string stored)
    {
        var read = UserMetadataJson.Deserialize(stored);

        read.ShouldNotBeNull();
        Describe(read).ShouldBe(Describe(LegacyUserMetadataJson.Deserialize(stored)));
    }

    [Test]
    public void TheDeepCopy_IsEqualToTheOriginal_AndSharesNothingWithIt()
    {
        var original = Dict(("roles", new List<object> { "a", "b" }), ("n", Dict(("k", new List<object?> { 1L }))));

        var copy = UserMetadataJson.Clone(original);

        Describe(copy).ShouldBe(Describe(original));
        ((List<object>)original["roles"]).Add("c");
        ((List<object>)copy["roles"]).Count.ShouldBe(2);
    }

    // The comparer ------------------------------------------------------------------------------------------------

    [Test]
    public void TwoMetadataWithTheSameValues_AreTheSame_EvenWhenTheirListsAreDifferentInstances()
    {
        var stored = Dict(("roles", new List<object> { "a", "b" }), ("quota", 5L));
        var fromTheProvider = Dict(("quota", 5L), ("roles", new List<object> { "a", "b" }));

        stored.IsSameAs(fromTheProvider).ShouldBeTrue("a login that returns the same roles changes nothing");
        fromTheProvider.IsSameAs(stored).ShouldBeTrue();
    }

    [Test]
    public void ARoleListThatDiffers_OrAValueNestedDeepInside_IsAChange()
    {
        var stored = Dict(("roles", new List<object> { "a", "b" }), ("n", Dict(("k", new List<object?> { 1L, "x" }))));

        stored.IsSameAs(Dict(("roles", new List<object> { "b", "a" }), ("n", Dict(("k", new List<object?> { 1L, "x" })))))
            .ShouldBeFalse("a list is ordered");
        stored.IsSameAs(Dict(("roles", new List<object> { "a", "b" }), ("n", Dict(("k", new List<object?> { 1L, "y" })))))
            .ShouldBeFalse("a nested value changed");
        stored.IsSameAs(Dict(("roles", new List<object> { "a", "b" }))).ShouldBeFalse("a key is missing");
    }

    [Test]
    public void EF_DoesNotSeeAChange_WhenAnEqualMetadataIsAssigned_AndSeesOneWhenAValueChanged()
    {
        using var db = TestDb.Create<ApplicationDbContext>();
        string id;
        using (var seed = db.NewContext())
        {
            var user = new User
            {
                UserName = "usermetadata", Metadata = Dict(("roles", new List<object> { "a", "b" }), ("quota", 5L))
            };
            seed.Users.Add(user);
            seed.SaveChanges();
            id = user.Id;
        }

        using var context = db.NewContext();
        var loaded = context.Users.Single(u => u.Id == id);

        loaded.Metadata = Dict(("roles", new List<object> { "a", "b" }), ("quota", 5L));
        context.ChangeTracker.DetectChanges();
        context.Entry(loaded).Property(u => u.Metadata).IsModified.ShouldBeFalse(
            "equal values in other instances are not a change");

        ((List<object>)loaded.Metadata["roles"]).Add("c");
        context.ChangeTracker.DetectChanges();
        context.Entry(loaded).Property(u => u.Metadata).IsModified.ShouldBeTrue(
            "a value changed inside a list is a change (the snapshot is a deep copy)");

        context.SaveChanges();
        using var check = db.NewContext();
        Describe(check.Users.Single(u => u.Id == id).Metadata)
            .ShouldBe(Describe(Dict(("roles", new List<object> { "a", "b", "c" }), ("quota", 5L))));
    }

    private sealed class SaveCounter : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public int Saves;

        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> SavingChanges(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result)
        {
            if (eventData.Context!.ChangeTracker.HasChanges())
                Saves++;
            return result;
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.HasChanges())
                Saves++;
            return ValueTask.FromResult(result);
        }
    }

    [Test]
    public async Task AProviderThatSendsTheSameMetadata_DoesNotWriteTheUserAgain_AndOneThatSendsAnotherDoes()
    {
        var counter = new SaveCounter();
        using var db = TestDb.Create<ApplicationDbContext>(options => options.AddInterceptors(counter));
        string id;
        using (var seed = db.NewContext())
        {
            var user = new User
            {
                UserName = "usermetadata", Metadata = Dict(("displayName", "Mario"), ("groups", new List<object> { "staff", "gis" }))
            };
            seed.Users.Add(user);
            seed.SaveChanges();
            id = user.Id;
        }

        var store = new Mock<IUserStore<User>>();
        var context = db.NewContext();
        var provisioning = new UserProvisioning(
            new Mock<UserManager<User>>(store.Object, null!, null!, null!, null!, null!, null!, null!, null!).Object,
            new Mock<RoleManager<IdentityRole>>(new Mock<IRoleStore<IdentityRole>>().Object, null!, null!, null!, null!).Object,
            context, new Mock<IPasswordPolicyValidator>().Object, TestSettings.Options<AccountOptions>(TestSettings.Mutable()),
            new Mock<IOrganizationsManager>().Object, NullLogger<UserProvisioning>.Instance);
        var loaded = context.Users.Single(u => u.Id == id);
        counter.Saves = 0;

        await provisioning.SyncFromProviderAsync(loaded,
            Dict(("displayName", "Mario"), ("groups", new List<object> { "staff", "gis" })));
        counter.Saves.ShouldBe(0, "the provider sent what is stored: nothing to write");

        await provisioning.SyncFromProviderAsync(loaded,
            Dict(("displayName", "Mario"), ("groups", new List<object> { "staff", "gis", "admins" })));
        counter.Saves.ShouldBe(1, "a group was added: one write");
    }

    [TestCaseSource(nameof(CorpusCases))]
    public void ARowOfAnOlderRegistry_IsReadThroughEF_AndAnUnchangedUserIsNotWrittenBack(Dictionary<string, object> metadata)
    {
        using var db = TestDb.Create<ApplicationDbContext>();
        string id;
        using (var seed = db.NewContext())
        {
            var user = new User { UserName = "usermetadata" };
            seed.Users.Add(user);
            seed.SaveChanges();
            id = user.Id;
            // The row as an older Registry wrote it
            seed.Database.ExecuteSqlRaw("UPDATE AspNetUsers SET Metadata = {0}", LegacyUserMetadataJson.Serialize(metadata));
        }

        using var context = db.NewContext();
        var loaded = context.Users.Single(u => u.Id == id);

        Describe(loaded.Metadata).ShouldBe(Describe(metadata));
        context.ChangeTracker.DetectChanges();
        context.ChangeTracker.HasChanges().ShouldBeFalse("reading a row is not a modification of it");
    }

    [Test]
    public void ANullOrEmptyColumn_IsAnEmptyMetadata_AsBefore()
    {
        using var db = TestDb.Create<ApplicationDbContext>();
        string id;
        using (var seed = db.NewContext())
        {
            var user = new User { UserName = "usermetadata" };
            seed.Users.Add(user);
            seed.SaveChanges();
            id = user.Id;
            seed.Database.ExecuteSqlRaw("UPDATE AspNetUsers SET Metadata = 'null'");
        }

        using var context = db.NewContext();
        context.Users.Single(u => u.Id == id).Metadata.ShouldBeEmpty();
        Encoding.UTF8.GetByteCount(UserMetadataJson.Serialize(new Dictionary<string, object>())).ShouldBe(2);
    }
}
