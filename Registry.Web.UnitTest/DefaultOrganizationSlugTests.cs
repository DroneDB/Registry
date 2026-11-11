using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Initialization;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Registry.Web.Test;

/// <summary>
/// The default organization of a user after the slug rule changed (FR-ORG-14: a slug no longer ends with '-' or '_'):
/// a user name that ends with '-', '_', '.', '@' or '+' has another slug now, and the organization made with the former
/// one is still found, at the start of the Registry for the admin (no second, empty admin organization) and by
/// <c>share/init</c> without an organization (by the owner and the default slug rule, not by a prefix of the name).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~DefaultOrganizationSlugTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public class DefaultOrganizationSlugTests
{
    [TestCase("ops.", "ops-")]
    [TestCase("ops_", "ops_")]
    [TestCase("pilot@", "pilot-")]
    [TestCase("john.doe", "john-doe")]
    [TestCase("a", "a0")]
    [TestCase("Ørsted", "0-rsted")]
    public void ToLegacySlug_IsTheFormerRule(string name, string slug) => name.ToLegacySlug().ShouldBe(slug);

    [Test]
    public void DefaultOrganizationSlugs_AreTheCurrentRuleThenTheFormerOne()
    {
        Extenders.DefaultOrganizationSlugs("ops.").ShouldBe(["ops", "ops-"]);
        Extenders.DefaultOrganizationSlugs("ops+").ShouldBe(["ops", "ops-"]);
        Extenders.DefaultOrganizationSlugs("alice").ShouldBe(["alice"]);
    }

    [TestCase("ops", "ops.", 0)]
    [TestCase("ops-", "ops.", 1)]
    [TestCase("ops-2", "ops.", 2)]
    [TestCase("ops--1", "ops.", 2)]
    [TestCase("alice-1", "alice", 2)]
    [TestCase("opsx", "ops.", null)]
    [TestCase("ops-x", "ops.", null)]
    [TestCase("ops-", "ops", null)]
    [TestCase("pilotage", "pilot.", null)]
    public void DefaultOrganizationSlugRank_MatchesOnlyTheDefaultSlugs(string slug, string userName, int? rank) =>
        Extenders.DefaultOrganizationSlugRank(slug, userName).ShouldBe(rank);

    #region Admin organization at start

    private const string AdminName = "ops.";

    private static async Task ProvisionAsync(RegistryWebFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var options = new IdentityProviderOptions
        {
            DefaultAdmin = new AdminInfo
            {
                UserName = AdminName,
                Password = RegistryWebFactory.AdminPassword,
                Email = "ops@example.com"
            }
        };
        var provisioner = ActivatorUtilities.CreateInstance<DefaultAdminProvisioner>(scope.ServiceProvider,
            MsOptions.Create(options));
        await provisioner.EnsureAsync(CancellationToken.None);
    }

    private static async Task<T> WithContextAsync<T>(RegistryWebFactory factory, Func<RegistryContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<RegistryContext>());
    }

    // An earlier start with the former rule made "ops-"
    private static async Task<string> MakeTheAdminOrganizationLegacyAsync(RegistryWebFactory factory, bool withoutOwner)
    {
        string adminId;
        using (var scope = factory.Services.CreateScope())
            adminId = (await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByNameAsync(AdminName))!.Id;

        return await WithContextAsync(factory, async context =>
        {
            // The slug is the key: the organization of the current rule is replaced with one of the former rule
            var org = await context.Organizations.SingleAsync(o => o.OwnerId == adminId);
            context.Organizations.Remove(org);
            await context.SaveChangesAsync();
            Seed.Organization(context, "ops-", name: org.Name, ownerId: withoutOwner ? null : adminId);
            await context.SaveChangesAsync();
            return adminId;
        });
    }

    [Test]
    public async Task TheAdminOrganization_MadeWithTheFormerSlug_IsFoundByItsOwner_AtTheNextStart()
    {
        using var factory = new RegistryWebFactory();
        await ProvisionAsync(factory);
        var adminId = await MakeTheAdminOrganizationLegacyAsync(factory, withoutOwner: false);

        await ProvisionAsync(factory);

        (await WithContextAsync(factory, context =>
                context.Organizations.Where(o => o.OwnerId == adminId).Select(o => o.Slug).ToListAsync()))
            .ShouldBe(["ops-"]);
    }

    [Test]
    public async Task TheAdminOrganization_MadeWithTheFormerSlug_IsFoundByThatSlug_WhenItHasNoOwner()
    {
        using var factory = new RegistryWebFactory();
        await ProvisionAsync(factory);
        await MakeTheAdminOrganizationLegacyAsync(factory, withoutOwner: true);

        await ProvisionAsync(factory);

        (await WithContextAsync(factory, context =>
                context.Organizations.Where(o => o.Slug == "ops" || o.Slug == "ops-").Select(o => o.Slug).ToListAsync()))
            .ShouldBe(["ops-"]);
    }

    // The default organization is the one with a default slug, not any organization the admin owns
    [Test]
    public async Task TheAdminOrganization_IsCreated_WhenTheAdminOwnsOnlyAnotherOrganization()
    {
        using var factory = new RegistryWebFactory();
        await ProvisionAsync(factory);
        var adminId = await ReplaceTheAdminOrganizationAsync(factory, "projects");

        await ProvisionAsync(factory);

        (await WithContextAsync(factory, context =>
                context.Organizations.Where(o => o.OwnerId == adminId).OrderBy(o => o.Slug).Select(o => o.Slug).ToListAsync()))
            .ShouldBe(["ops", "projects"]);
    }

    [Test]
    public async Task TheAdminOrganization_WithTheSuffixOfATakenSlug_IsTheDefaultOne()
    {
        using var factory = new RegistryWebFactory();
        await ProvisionAsync(factory);
        var adminId = await ReplaceTheAdminOrganizationAsync(factory, "ops-2", "projects");

        await ProvisionAsync(factory);

        (await WithContextAsync(factory, context =>
                context.Organizations.Where(o => o.OwnerId == adminId).OrderBy(o => o.Slug).Select(o => o.Slug).ToListAsync()))
            .ShouldBe(["ops-2", "projects"]);
    }

    // The slug of the default organization is another user's: the admin gets the next free one, not a failed start
    [Test]
    public async Task TheAdminOrganization_IsMadeWithTheNextSlug_WhenAnotherUserOwnsTheDefaultOne()
    {
        using var factory = new RegistryWebFactory();
        await ProvisionAsync(factory);
        var adminId = await ReplaceTheAdminOrganizationAsync(factory, "projects");
        var other = await factory.CreateUserAsync("someone");
        await WithContextAsync(factory, async context =>
        {
            Seed.Organization(context, "ops", name: "ops", ownerId: other.Id);
            return await context.SaveChangesAsync();
        });

        await ProvisionAsync(factory);

        (await WithContextAsync(factory, context =>
                context.Organizations.Where(o => o.OwnerId == adminId).OrderBy(o => o.Slug).Select(o => o.Slug).ToListAsync()))
            .ShouldBe(["ops-1", "projects"]);
    }

    // The organizations of the admin are replaced with the ones of these slugs, owned by the admin
    private static async Task<string> ReplaceTheAdminOrganizationAsync(RegistryWebFactory factory, params string[] slugs)
    {
        string adminId;
        using (var scope = factory.Services.CreateScope())
            adminId = (await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByNameAsync(AdminName))!.Id;

        await WithContextAsync(factory, async context =>
        {
            context.Organizations.RemoveRange(await context.Organizations.Where(o => o.OwnerId == adminId).ToListAsync());
            await context.SaveChangesAsync();
            foreach (var slug in slugs)
                Seed.Organization(context, slug, name: slug, ownerId: adminId);
            return await context.SaveChangesAsync();
        });
        return adminId;
    }

    [Test]
    public async Task TheAdminOrganization_IsMadeWithTheCurrentSlug_AtTheFirstStart()
    {
        using var factory = new RegistryWebFactory();

        await ProvisionAsync(factory);

        (await WithContextAsync(factory, context =>
                context.Organizations.Where(o => o.Slug == "ops" || o.Slug == "ops-").Select(o => o.Slug).ToListAsync()))
            .ShouldBe(["ops"]);
    }

    #endregion

    #region share/init without an organization

    private static RegistryWebFactory ShareFactory()
    {
        var factory = new RegistryWebFactory();
        factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Throws(new DdbException("Key not found"));
        factory.DdbWrapper.Setup(d => d.MetaSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string _, string key, string _, string? _) =>
                new Meta { Id = key, Data = TestJson.Parse("null"), ModifiedTime = DateTime.UtcNow });
        factory.DdbWrapper.Setup(d => d.Info(It.IsAny<string[]>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns(() => [new Entry { Path = "", Type = EntryType.DroneDB, Size = 0 }]);
        return factory;
    }

    private static async Task<string> ShareInitOrganizationAsync(RegistryWebFactory factory, string userName)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/share/init")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["DatasetName"] = "Survey" })
        }.AsUser(userName);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);

        var token = JsonNode.Parse(text)!["token"]!.GetValue<string>();
        return await WithContextAsync(factory, async context =>
            (await context.Batches.AsNoTracking().Include(b => b.Dataset.Organization).SingleAsync(b => b.Token == token))
            .Dataset.Organization.Slug);
    }

    // The former slug of "pilot." is "pilot-"; another organization of the user whose name starts like the user name
    // is not the default one
    [Test]
    public async Task ShareInit_WithoutAnOrganization_UsesTheDefaultOrganizationOfTheFormerSlug_NotAnotherOneWithALikeName()
    {
        using var factory = ShareFactory();
        var user = await factory.CreateUserAsync("pilot.");
        await WithContextAsync(factory, async context =>
        {
            var older = Seed.Organization(context, "pilotage", name: "pilotage", ownerId: user.Id);
            older.CreationDate = DateTime.UtcNow.AddDays(-2);
            Seed.Organization(context, "pilot-", name: "pilot.", ownerId: user.Id).CreationDate = DateTime.UtcNow.AddDays(-1);
            return await context.SaveChangesAsync();
        });

        (await ShareInitOrganizationAsync(factory, "pilot.")).ShouldBe("pilot-");

        (await WithContextAsync(factory, context =>
                context.Organizations.Where(o => o.OwnerId == user.Id).OrderBy(o => o.Slug).Select(o => o.Slug).ToListAsync()))
            .ShouldBe(["pilot-", "pilotage"]);
    }

    // The default organization of "john.doe" is "john-doe": its name ("john.doe") does not start with the slug, and
    // the dataset still goes there, with no new organization
    [Test]
    public async Task ShareInit_WithoutAnOrganization_FindsTheDefaultOrganizationBySlug_NotByName()
    {
        using var factory = ShareFactory();
        var user = await factory.CreateUserAsync("john.doe");
        await WithContextAsync(factory, async context =>
        {
            Seed.Organization(context, "john-doe", name: "john.doe", ownerId: user.Id);
            return await context.SaveChangesAsync();
        });

        (await ShareInitOrganizationAsync(factory, "john.doe")).ShouldBe("john-doe");

        (await WithContextAsync(factory, context => context.Organizations.CountAsync(o => o.OwnerId == user.Id)))
            .ShouldBe(1);
    }

    #endregion
}
