#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using NUnit.Framework;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;
using Registry.Test.Common;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// Follow-up of <c>IdentityMetadataRepairTests</c> (Registry.Web.IntegrationTest): <c>UsersManager</c> stored the
/// login provider's metadata as is. A provider that returns none (e.g. a remote auth server answering
/// <c>null</c>) made the login fail (null dereference in the comparison), and a null
/// <see cref="User.Metadata"/> would be written as NULL (EF does not run value converters for null),
/// which the repaired column (NOT NULL, <c>{}</c> when empty) rejects and legacy nullable SQLite columns
/// accept. <see cref="User.Metadata"/> is now the single owner of the rule: null is stored as an empty
/// dictionary and a row that reads back as null is seen as empty.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UserMetadataNullGuardTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public class UserMetadataNullGuardTests
{
    private const string EmptyMetadata = "{}";
    private const string UserName = "usermetadatanullguard-metadata-user";

    [Test]
    public void AssigningNull_StoresAnEmptyDictionary()
    {
        var user = new User { Metadata = new Dictionary<string, object> { ["maxStorage"] = 5 } };

        user.Metadata = null;

        user.Metadata.ShouldNotBeNull();
        user.Metadata.ShouldBeEmpty();
    }

    [Test]
    public async Task SavingAUserWithNullMetadata_WritesEmptyJson()
    {
        await using var context = TestDb.CreateContext<ApplicationDbContext>();
        context.Users.Add(new User { UserName = UserName, Metadata = null });

        await context.SaveChangesAsync();

        (await StoredMetadata(context, UserName)).ShouldBe(EmptyMetadata);
    }

    [Test]
    public async Task ARowThatReadsBackAsNull_IsSeenAsEmpty()
    {
        await using var context = TestDb.CreateContext<ApplicationDbContext>(seed: db =>
        {
            db.Users.Add(new User { UserName = UserName });
            db.SaveChanges();
            // The converter turns the JSON literal into a null dictionary, as a legacy NULL row reads
            db.Database.ExecuteSql($"UPDATE AspNetUsers SET Metadata = 'null' WHERE UserName = {UserName}");
        });

        var user = await context.Users.SingleAsync(u => u.UserName == UserName);

        user.Metadata.ShouldNotBeNull();
        user.Metadata.ShouldBeEmpty();
    }

    [Test]
    public async Task Login_WhenTheProviderReturnsNoMetadata_SucceedsAndStoresEmptyMetadata()
    {
        var loginManager = new Mock<ILoginManager>();
        loginManager.Setup(m => m.Capabilities).Returns(AuthProviderCapabilities.Local);
        loginManager.Setup(m => m.CheckAccess(RegistryWebFactory.AdminUserName, It.IsAny<string>()))
            .ReturnsAsync(new LoginResultDto
            {
                Success = true, UserName = RegistryWebFactory.AdminUserName, Metadata = null
            });

        using var factory = new RegistryWebFactory(configureServices: services =>
        {
            services.RemoveAll<ILoginManager>();
            services.AddScoped(_ => loginManager.Object);
        });
        using var client = factory.CreateClient();

        // The user starts with metadata, so the provider's "none" is a change that gets saved
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var admin = await context.Users.SingleAsync(u => u.UserName == RegistryWebFactory.AdminUserName);
            admin.Metadata = new Dictionary<string, object> { ["maxStorage"] = 5 };
            await context.SaveChangesAsync();
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = RegistryWebFactory.AdminUserName,
            ["password"] = RegistryWebFactory.AdminPassword
        });
        using var response = await client.PostAsync("/users/authenticate", form);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await StoredMetadata(context, RegistryWebFactory.AdminUserName)).ShouldBe(EmptyMetadata);
        }
    }

    private static Task<string> StoredMetadata(ApplicationDbContext context, string userName) =>
        context.Database
            .SqlQuery<string>($"SELECT Metadata AS Value FROM AspNetUsers WHERE UserName = {userName}")
            .SingleAsync();
}
