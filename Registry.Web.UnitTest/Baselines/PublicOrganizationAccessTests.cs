using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// Public organizations through HTTP (FR-ORG-02, FR-ORG-11, D-003, §7.1 <c>GET /orgs</c> and <c>GET /orgs/{org}</c>):
/// a signed-in user who is not a member reads a Public organization of another user (200, read-only permissions) and
/// finds it in <c>GET /orgs</c> with and without <c>ownedOnly</c>; a member finds its organization in
/// <c>GET /orgs?ownedOnly=true</c>; a Public organization whose owner is deactivated is neither readable nor listed by
/// those who are not members; a private organization still answers 403.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~PublicOrganizationAccessTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class PublicOrganizationAccessTests : DatasetAccessFixture
{
    private const string PublicOrg = "datasetaccess-public-org";
    private const string GoneOwnerOrg = "datasetaccess-gone-owner-org";
    private const string GoneOwnerUser = "datasetaccess-gone-owner";

    protected override async Task SeedAsync(RegistryContext context)
    {
        Seed.Organization(context, PublicOrg, ownerId: UserIds[OwnerUser], isPublic: true);

        var goneOwner = await Factory.CreateUserAsync(GoneOwnerUser);
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            (await users.AddToRoleAsync((await users.FindByIdAsync(goneOwner.Id))!,
                ApplicationDbContext.DeactivatedRoleName)).Succeeded.ShouldBeTrue();
        }
        Seed.Organization(context, GoneOwnerOrg, ownerId: goneOwner.Id, isPublic: true);

        await context.SaveChangesAsync();
    }

    private async Task<string[]> ListAsync(Caller caller, bool? ownedOnly)
    {
        var url = ownedOnly == null ? "/orgs" : "/orgs?ownedOnly=" + (ownedOnly.Value ? "true" : "false");
        var (response, body, _) = await SendAsync(Request(HttpMethod.Get, url, caller));
        ((int)response.StatusCode).ShouldBe(200, body);
        return JsonNode.Parse(body)!.AsArray().Select(o => o!["slug"]!.GetValue<string>()).ToArray();
    }

    [Test]
    public async Task PublicOrganizationOfAnotherUser_NonMember_Reads_ReadOnly()
    {
        var (response, body, org) = await SendAsync(Request(HttpMethod.Get, "/orgs/" + PublicOrg, Caller.Stranger));

        ((int)response.StatusCode).ShouldBe(200, body);
        var permissions = org.ShouldNotBeNull()["permissions"].ShouldNotBeNull();
        permissions["canRead"]!.GetValue<bool>().ShouldBeTrue();
        permissions["canWrite"]!.GetValue<bool>().ShouldBeFalse();
        permissions["canDelete"]!.GetValue<bool>().ShouldBeFalse();
        permissions["canManageMembers"]!.GetValue<bool>().ShouldBeFalse();
    }

    [TestCase(null)]
    [TestCase(false)]
    [TestCase(true)]
    public async Task PublicOrganizationOfAnotherUser_IsListed(bool? ownedOnly)
    {
        (await ListAsync(Caller.Stranger, ownedOnly)).ShouldContain(PublicOrg);
    }

    [Test]
    public async Task Member_FindsItsOrganization_InItsOwnList()
    {
        (await ListAsync(Caller.Reader, ownedOnly: true)).ShouldContain(PrivateOrg);
    }

    [TestCase(null)]
    [TestCase(true)]
    public async Task PrivateOrganization_IsNotListed_ToANonMember(bool? ownedOnly)
    {
        (await ListAsync(Caller.Stranger, ownedOnly)).ShouldNotContain(PrivateOrg);
    }

    [Test]
    public async Task PrivateOrganization_NonMember_Is403()
    {
        var (response, _, problem) = await SendAsync(Request(HttpMethod.Get, "/orgs/" + PrivateOrg, Caller.Stranger));

        ShouldBeProblem(response, problem, 403, "The current user is not allowed to read this organization");
    }

    [Test]
    public async Task PublicOrganizationOfADeactivatedOwner_IsNotReadable()
    {
        var (stranger, _, strangerProblem) =
            await SendAsync(Request(HttpMethod.Get, "/orgs/" + GoneOwnerOrg, Caller.Stranger));
        ShouldBeProblem(stranger, strangerProblem, 403, "The current user is not allowed to read this organization");

        var (anonymous, _, anonymousProblem) =
            await SendAsync(Request(HttpMethod.Get, "/orgs/" + GoneOwnerOrg, Caller.Anonymous));
        ShouldBeProblem(anonymous, anonymousProblem, 401, "The current user is not allowed to read this organization");
    }

    [TestCase(null)]
    [TestCase(true)]
    public async Task PublicOrganizationOfADeactivatedOwner_IsNotListed(bool? ownedOnly)
    {
        (await ListAsync(Caller.Stranger, ownedOnly)).ShouldNotContain(GoneOwnerOrg);
    }
}
