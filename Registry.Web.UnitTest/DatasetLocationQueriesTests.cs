using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Datasets;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The locations of the datasets of an organization (<c>GET /orgs/{org}/locations</c>) over the real access rules: a
/// caller gets the datasets the dataset list gives them, and the datasets that are unlisted only when they belong to the
/// organization.
/// </summary>
[TestFixture]
public class DatasetLocationQueriesTests
{
    private const string PrivateOrg = DatasetVisibilityWorld.PrivateOrg;
    private const string PublicOrg = DatasetVisibilityWorld.PublicOrg;

    private DatasetVisibilityWorld _world = null!;
    private Mock<IDatasetLocationProvider> _locations = null!;
    private User _owner = null!;
    private User _member = null!;
    private User _outsider = null!;
    private User _administrator = null!;
    private Dictionary<string, Guid> _refs = null!;

    [SetUp]
    public void Setup()
    {
        _world = new DatasetVisibilityWorld();
        (_owner, _member, _outsider, _administrator) = (_world.Owner, _world.Member, _world.Outsider, _world.Administrator);
        _refs = _world.Refs;

        // Every dataset is somewhere, but the one called "c-private" has no location at all
        _locations = new Mock<IDatasetLocationProvider>();
        _locations.Setup(l => l.GetAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string org, Guid dsRef, CancellationToken _) =>
                _refs.Single(r => r.Value == dsRef).Key.EndsWith("c-private", StringComparison.Ordinal)
                    ? null
                    : new DatasetLocation([1, 2, 3, 4]));
    }

    [TearDown]
    public void TearDown() => _world.Dispose();

    private Task<IReadOnlyList<Models.DTO.DatasetLocationDto>> Locations(User? caller, string orgSlug) =>
        new DatasetLocationQueries(_world.Datasets(caller), _world.Cache.Object, _world.DdbManager.Object,
            _locations.Object, NullLogger<DatasetLocationQueries>.Instance).List(orgSlug);

    private async Task<string[]> Slugs(User? caller, string orgSlug) =>
        (await Locations(caller, orgSlug)).Select(l => l.Slug).ToArray();

    [Test]
    public async Task TheOwner_GetsEveryDataset_BySlug_WithNameAndBox()
    {
        var all = await Locations(_owner, PrivateOrg);

        all.Select(l => l.Slug).ShouldBe(["a-public", "b-unlisted", "c-private"]);
        all[0].Name.ShouldBe("private-org a-public");
        all[0].Bbox.ShouldBe([1, 2, 3, 4]);
        all[2].Bbox.ShouldBeNull("a dataset without a location is listed with no box");
    }

    [Test]
    public async Task AnAdministrator_GetsEveryDataset()
    {
        (await Slugs(_administrator, PublicOrg)).ShouldBe(["a-public", "b-unlisted", "c-private"]);
        (await Slugs(_administrator, PrivateOrg)).ShouldBe(["a-public", "b-unlisted", "c-private"]);
    }

    [Test]
    public async Task AMember_GetsTheDatasetsOfTheOrganization_UnlistedOnesToo()
    {
        // A read-only member reads every dataset of the organization: a private one is theirs to see
        (await Slugs(_member, PrivateOrg)).ShouldBe(["a-public", "b-unlisted", "c-private"]);
        (await Slugs(_member, PublicOrg)).ShouldBe(["a-public", "b-unlisted", "c-private"]);
    }

    [Test]
    public async Task ASignedInNonMember_IsRefusedByTheOrganizationRule_AsTheDatasetListIs()
    {
        // The organization rule lets only anonymous callers read a public organization they do not belong to
        await Should.ThrowAsync<ForbiddenException>(() => Locations(_outsider, PublicOrg));
    }

    [Test]
    public async Task Anonymous_InAPublicOrganization_GetsTheListedPublicDatasetsOnly()
    {
        (await Slugs(null, PublicOrg)).ShouldBe(["a-public"]);
    }

    [Test]
    public async Task ANonMember_AndAnonymous_CannotReadAPrivateOrganization_EvenWithAPublicDatasetInIt()
    {
        await Should.ThrowAsync<ForbiddenException>(() => Locations(_outsider, PrivateOrg));
        await Should.ThrowAsync<ForbiddenException>(() => Locations(null, PrivateOrg));
    }

    [Test]
    public async Task AnOrganizationThatDoesNotExist_IsNotFound()
    {
        await Should.ThrowAsync<NotFoundException>(() => Locations(_owner, "nowhere"));
    }

    [Test]
    public async Task TheDatasetsOfAnotherOrganization_AreNotInTheAnswer()
    {
        var ofPublic = await Locations(_owner, PublicOrg);

        ofPublic.ShouldAllBe(l => l.Name!.StartsWith(PublicOrg, StringComparison.Ordinal));
    }

    [Test]
    public async Task TheLocationsAreReadForTheDatasetsOfTheAnswerOnly()
    {
        await Slugs(null, PublicOrg);

        _locations.Verify(l => l.GetAsync(PublicOrg, _refs["public-org/a-public"], It.IsAny<CancellationToken>()), Times.Once);
        _locations.Verify(l => l.GetAsync(It.IsAny<string>(), _refs["public-org/b-unlisted"], It.IsAny<CancellationToken>()), Times.Never);
        _locations.Verify(l => l.GetAsync(It.IsAny<string>(), _refs["public-org/c-private"], It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ADatasetWhoseNameCannotBeRead_IsListedWithoutAName_AndTheOthersAreUnaffected()
    {
        // The owner skips the per-dataset checks: the row is read, then its folder is gone before its name is
        _world.NameFailures[_refs["private-org/b-unlisted"]] = new DdbException("Not a valid DroneDB directory");

        var all = await Locations(_owner, PrivateOrg);

        all.Select(l => l.Slug).ShouldBe(["a-public", "b-unlisted", "c-private"]);
        all[1].Name.ShouldBeNull();
        all[1].Bbox.ShouldBe([1, 2, 3, 4]);
        all[0].Name.ShouldBe("private-org a-public");
        all[2].Name.ShouldBe("private-org c-private");
    }

    [Test]
    public async Task ANameThatCannotBeReadForNow_OrACancellation_FailsTheMap()
    {
        // A busy index says nothing about the dataset: the caller answers 503 and the map is asked for again
        _world.NameFailures[_refs["private-org/b-unlisted"]] = new DdbBusyException("busy");
        await Should.ThrowAsync<DdbBusyException>(() => Locations(_owner, PrivateOrg));

        _world.NameFailures[_refs["private-org/b-unlisted"]] = new OperationCanceledException();
        await Should.ThrowAsync<OperationCanceledException>(() => Locations(_owner, PrivateOrg));
    }
}
