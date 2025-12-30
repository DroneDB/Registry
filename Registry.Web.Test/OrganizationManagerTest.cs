using Shouldly;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Identity.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Registry.Ports;
using Registry.Test.Common;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Test;

[TestFixture]
public class OrganizationManagerTest : TestBase
{
    private Mock<IAuthManager> _authManagerMock;
    private Mock<IOptions<AppSettings>> _appSettingsMock;
    private Mock<IDatasetsManager> _datasetManagerMock;
    private Mock<IHttpContextAccessor> _httpContextAccessorMock;
    private Mock<IDdbManager> _ddbManagerMock;
    private ILogger<OrganizationsManager> _logger;
    private OrganizationsManager _organizationsManager;
    private RegistryContext _context;
    private ApplicationDbContext _appContext;
    private IUtils _webUtils;

    [SetUp]
    public async Task Setup()
    {
        // Initialize mocks
        _authManagerMock = new Mock<IAuthManager>();
        _appSettingsMock = new Mock<IOptions<AppSettings>>();
        _datasetManagerMock = new Mock<IDatasetsManager>();
        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        _ddbManagerMock = new Mock<IDdbManager>();
        _logger = CreateTestLogger<OrganizationsManager>();

        // Set up contexts
        _context = await CreateTestRegistryContext();
        _appContext = await CreateTestApplicationContext();

        // Set up default auth manager behavior
        SetupDefaultAuthManagerBehavior();

        // Initialize utils and manager
        _webUtils = new WebUtils(_authManagerMock.Object, _context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbManagerMock.Object);

        _organizationsManager = new OrganizationsManager(
            _authManagerMock.Object,
            _context,
            _webUtils,
            _datasetManagerMock.Object,
            _appContext,
            _logger);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _context.DisposeAsync();
        await _appContext.DisposeAsync();
    }

    #region Tests

    [Test]
    public async Task List_AsAdmin_ReturnsAllOrganizations()
    {
        // Arrange
        SetupAdminUser();

        // Act
        var organizations = (await _organizationsManager.List()).ToArray();

        // Assert
        organizations.Count().ShouldBe(1);
        var publicOrg = organizations.First();
        AssertPublicOrganization(publicOrg);
    }

    [Test]
    public async Task AddNew_ValidOrganization_CreatesSuccessfully()
    {
        // Arrange
        SetupAdminUser();
        var newOrg = CreateTestOrganizationDto("test-org");

        // Act
        var result = await _organizationsManager.AddNew(newOrg);

        // Assert
        result.ShouldNotBeNull();
        result.Slug.ShouldBe(newOrg.Slug);
        result.Name.ShouldBe(newOrg.Name);
        result.Description.ShouldBe(newOrg.Description);
    }

    [Test]
    public async Task Get_ExistingOrganization_ReturnsCorrectOrganization()
    {
        // Arrange
        SetupAdminUser();

        // Act
        var result = await _organizationsManager.Get(MagicStrings.PublicOrganizationSlug);

        // Assert
        result.ShouldNotBeNull();
        AssertPublicOrganization(result);
    }

    [Test]
    public async Task Edit_ValidUpdate_UpdatesSuccessfully()
    {
        // Arrange
        SetupAdminUser();
        var updateDto = new OrganizationDto
        {
            Name = "Updated Public Org",
            Description = "Updated description",
            IsPublic = true
        };

        // Act
        await _organizationsManager.Edit(MagicStrings.PublicOrganizationSlug, updateDto);
        var updated = await _organizationsManager.Get(MagicStrings.PublicOrganizationSlug);

        // Assert
        updated.Name.ShouldBe(updateDto.Name);
        updated.Description.ShouldBe(updateDto.Description);
    }

    [Test]
    public async Task Delete_ExistingOrganization_DeletesSuccessfully()
    {
        // Arrange
        SetupAdminUser();
        _datasetManagerMock.Setup(x => x.Delete(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        // Act
        await _organizationsManager.Delete(MagicStrings.PublicOrganizationSlug);
        var organizations = await _organizationsManager.List();

        // Assert
        organizations.ShouldBeEmpty();
    }

    #region Authorization Tests

    [Test]
    public async Task List_StandardUser_ReturnsOnlyOwnedAndPublicOrganizations()
    {
        // Arrange
        var standardUser = SetupStandardUser("standard-user");
        var ownedOrg = await CreateOrganizationForUser(standardUser.Id);

        // Act
        var organizations = (await _organizationsManager.List()).ToArray();

        // Assert
        organizations.Count().ShouldBe(2); // Public org + owned org
        organizations.ShouldContain(org => org.Slug == MagicStrings.PublicOrganizationSlug);
        organizations.ShouldContain(org => org.Slug == ownedOrg.Slug);
    }

    [Test]
    public async Task List_AnonymousUser_ThrowsUnauthorizedException()
    {
        // Arrange
        SetupAnonymousUser();

        // Act & Assert
        var action = () => _organizationsManager.List();
        await Should.ThrowAsync<UnauthorizedException>(action);
    }

    [Test]
    public async Task AddNew_StandardUser_CanOnlyCreateOwnOrganization()
    {
        // Arrange
        var standardUser = SetupStandardUser("standard-user");
        var newOrg = CreateTestOrganizationDto("test-org");
        newOrg.Owner = "different-user"; // Trying to create org for different user

        // Act & Assert
        var action = () => _organizationsManager.AddNew(newOrg);
        await Should.ThrowAsync<UnauthorizedException>(action);
    }

    [Test]
    public async Task AddNew_AnonymousUser_ThrowsUnauthorizedException()
    {
        // Arrange
        SetupAnonymousUser();
        var newOrg = CreateTestOrganizationDto("test-org");

        // Act & Assert
        var action = () => _organizationsManager.AddNew(newOrg);
        await Should.ThrowAsync<UnauthorizedException>(action);
    }

    [Test]
    public async Task Edit_StandardUser_CanOnlyEditOwnOrganization()
    {
        // Arrange
        var standardUser = SetupStandardUser("standard-user");
        var ownedOrg = await CreateOrganizationForUser(standardUser.Id);
        var updateDto = new OrganizationDto
        {
            Name = "Updated Org",
            Description = "Updated description",
            IsPublic = false
        };

        // Act
        await _organizationsManager.Edit(ownedOrg.Slug, updateDto);
        var updated = await _organizationsManager.Get(ownedOrg.Slug);

        // Assert
        updated.Name.ShouldBe(updateDto.Name);
        updated.Description.ShouldBe(updateDto.Description);
        updated.Owner.ShouldBe(standardUser.UserName);
    }

    /*[Test]
    public async Task Edit_StandardUser_CannotEditOthersOrganization()
    {
        // Arrange
        SetupStandardUser("standard-user");
        var updateDto = new OrganizationDto
        {
            Name = "Updated Public Org",
            Description = "Updated description",
            IsPublic = true
        };


        // Act & Assert
        var action = () => _organizationsManager.Edit(MagicStrings.PublicOrganizationSlug, updateDto);
        await Should.ThrowAsync<UnauthorizedException>(action)
            .WithMessage("Invalid user");
    }*/


    [Test]
    public async Task Delete_StandardUser_CanOnlyDeleteOwnOrganization()
    {
        // Arrange
        var standardUser = SetupStandardUser("standard-user");
        var ownedOrg = await CreateOrganizationForUser(standardUser.Id);
        _datasetManagerMock.Setup(x => x.Delete(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        // Act
        await _organizationsManager.Delete(ownedOrg.Slug);

        // Assert
        var organizations = await _organizationsManager.List();
        organizations.ShouldNotContain(org => org.Slug == ownedOrg.Slug);
    }
/*
    [Test]
    public async Task Delete_StandardUser_CannotDeleteOthersOrganization()
    {
        // Arrange
        SetupStandardUser("standard-user");

        // Act & Assert
        var action = () => _organizationsManager.Delete(MagicStrings.PublicOrganizationSlug);
        await Should.ThrowAsync<UnauthorizedException>(action)
            .WithMessage("Invalid user");
    }*/

    #endregion

    #region Merge Tests

    [Test]
    public async Task Merge_NonAdminUser_ThrowsUnauthorizedException()
    {
        // Arrange
        SetupStandardUser("standard-user");
        await CreateOrganizationWithDatasets("source-org", "admin-id", 1);
        await CreateOrganizationWithDatasets("dest-org", "admin-id", 0);

        // Act & Assert
        var action = () => _organizationsManager.Merge("source-org", "dest-org");
        await Should.ThrowAsync<UnauthorizedException>(action);
    }

    [Test]
    public async Task Merge_SourceSlugEmpty_ThrowsBadRequestException()
    {
        // Arrange
        SetupAdminUser();

        // Act & Assert
        var action = () => _organizationsManager.Merge("", "dest-org");
        await Should.ThrowAsync<BadRequestException>(action);
    }

    [Test]
    public async Task Merge_SourceSlugNull_ThrowsBadRequestException()
    {
        // Arrange
        SetupAdminUser();

        // Act & Assert
        var action = () => _organizationsManager.Merge(null, "dest-org");
        await Should.ThrowAsync<BadRequestException>(action);
    }

    [Test]
    public async Task Merge_DestSlugEmpty_ThrowsBadRequestException()
    {
        // Arrange
        SetupAdminUser();

        // Act & Assert
        var action = () => _organizationsManager.Merge("source-org", "");
        await Should.ThrowAsync<BadRequestException>(action);
    }

    [Test]
    public async Task Merge_DestSlugNull_ThrowsBadRequestException()
    {
        // Arrange
        SetupAdminUser();

        // Act & Assert
        var action = () => _organizationsManager.Merge("source-org", null);
        await Should.ThrowAsync<BadRequestException>(action);
    }

    [Test]
    public async Task Merge_SourceEqualsDestination_ThrowsBadRequestException()
    {
        // Arrange
        SetupAdminUser();

        // Act & Assert
        var action = () => _organizationsManager.Merge("same-org", "same-org");
        await Should.ThrowAsync<BadRequestException>(action);
    }

    [Test]
    public async Task Merge_PublicOrganizationAsSource_ThrowsBadRequestException()
    {
        // Arrange
        SetupAdminUser();
        await CreateOrganizationWithDatasets("dest-org", "admin-id", 0);

        // Act & Assert
        var action = () => _organizationsManager.Merge(MagicStrings.PublicOrganizationSlug, "dest-org");
        await Should.ThrowAsync<BadRequestException>(action);
    }

    [Test]
    public async Task Merge_WithDatasetsNoConflicts_MovesAllDatasets()
    {
        // Arrange
        SetupAdminUser();
        await CreateOrganizationWithDatasets("source-org", "admin-id", 3);
        await CreateOrganizationWithDatasets("dest-org", "admin-id", 0);

        _datasetManagerMock.Setup(x => x.MoveToOrganization(
            "source-org",
            It.Is<string[]>(slugs => slugs.Length == 3),
            "dest-org",
            ConflictResolutionStrategy.HaltOnConflict))
            .ReturnsAsync(new[]
            {
                new MoveDatasetResultDto { OriginalSlug = "dataset-0", NewSlug = "dataset-0", Success = true },
                new MoveDatasetResultDto { OriginalSlug = "dataset-1", NewSlug = "dataset-1", Success = true },
                new MoveDatasetResultDto { OriginalSlug = "dataset-2", NewSlug = "dataset-2", Success = true }
            });

        // Act
        var result = await _organizationsManager.Merge("source-org", "dest-org");

        // Assert
        result.ShouldNotBeNull();
        result.SourceOrgSlug.ShouldBe("source-org");
        result.DestinationOrgSlug.ShouldBe("dest-org");
        result.DatasetsMovedCount.ShouldBe(3);
        result.DatasetsFailedCount.ShouldBe(0);
        result.DatasetResults.Length.ShouldBe(3);
        result.SourceOrganizationDeleted.ShouldBeTrue();

        // Verify MoveToOrganization was called
        _datasetManagerMock.Verify(x => x.MoveToOrganization(
            "source-org",
            It.IsAny<string[]>(),
            "dest-org",
            ConflictResolutionStrategy.HaltOnConflict), Times.Once);
    }

    [Test]
    public async Task Merge_WithConflictsHaltOnConflict_ReportsFailedDatasets()
    {
        // Arrange
        SetupAdminUser();
        await CreateOrganizationWithDatasets("source-org", "admin-id", 2);
        await CreateOrganizationWithDatasets("dest-org", "admin-id", 0);

        _datasetManagerMock.Setup(x => x.MoveToOrganization(
            "source-org",
            It.IsAny<string[]>(),
            "dest-org",
            ConflictResolutionStrategy.HaltOnConflict))
            .ReturnsAsync(new[]
            {
                new MoveDatasetResultDto { OriginalSlug = "dataset-0", Success = true },
                new MoveDatasetResultDto { OriginalSlug = "dataset-1", Success = false, Error = "Conflict detected" }
            });

        // Act
        var result = await _organizationsManager.Merge("source-org", "dest-org");

        // Assert
        result.DatasetsMovedCount.ShouldBe(1);
        result.DatasetsFailedCount.ShouldBe(1);
        result.SourceOrganizationDeleted.ShouldBeFalse(); // Should not delete when there are failures
    }

    [Test]
    public async Task Merge_WithConflictsRenameStrategy_MovesWithRenamedSlugs()
    {
        // Arrange
        SetupAdminUser();
        await CreateOrganizationWithDatasets("source-org", "admin-id", 2);
        await CreateOrganizationWithDatasets("dest-org", "admin-id", 0);

        _datasetManagerMock.Setup(x => x.MoveToOrganization(
            "source-org",
            It.IsAny<string[]>(),
            "dest-org",
            ConflictResolutionStrategy.Rename))
            .ReturnsAsync(new[]
            {
                new MoveDatasetResultDto { OriginalSlug = "dataset-0", NewSlug = "dataset-0_1", Success = true },
                new MoveDatasetResultDto { OriginalSlug = "dataset-1", NewSlug = "dataset-1", Success = true }
            });

        // Act
        var result = await _organizationsManager.Merge("source-org", "dest-org", ConflictResolutionStrategy.Rename);

        // Assert
        result.DatasetsMovedCount.ShouldBe(2);
        result.DatasetsFailedCount.ShouldBe(0);
        result.SourceOrganizationDeleted.ShouldBeTrue();
        result.DatasetResults.ShouldContain(r => r.NewSlug == "dataset-0_1");
    }

    [Test]
    public async Task Merge_WithConflictsOverwriteStrategy_OverwritesExistingDatasets()
    {
        // Arrange
        SetupAdminUser();
        await CreateOrganizationWithDatasets("source-org", "admin-id", 2);
        await CreateOrganizationWithDatasets("dest-org", "admin-id", 0);

        _datasetManagerMock.Setup(x => x.MoveToOrganization(
            "source-org",
            It.IsAny<string[]>(),
            "dest-org",
            ConflictResolutionStrategy.Overwrite))
            .ReturnsAsync(new[]
            {
                new MoveDatasetResultDto { OriginalSlug = "dataset-0", NewSlug = "dataset-0", Success = true },
                new MoveDatasetResultDto { OriginalSlug = "dataset-1", NewSlug = "dataset-1", Success = true }
            });

        // Act
        var result = await _organizationsManager.Merge("source-org", "dest-org", ConflictResolutionStrategy.Overwrite);

        // Assert
        result.DatasetsMovedCount.ShouldBe(2);
        result.DatasetsFailedCount.ShouldBe(0);
        result.SourceOrganizationDeleted.ShouldBeTrue();

        // Verify MoveToOrganization was called with Overwrite strategy
        _datasetManagerMock.Verify(x => x.MoveToOrganization(
            "source-org",
            It.IsAny<string[]>(),
            "dest-org",
            ConflictResolutionStrategy.Overwrite), Times.Once);
    }

    [Test]
    public async Task Merge_WithDeleteSourceOrganizationFalse_PreservesSourceOrganization()
    {
        // Arrange
        SetupAdminUser();
        await CreateOrganizationWithDatasets("source-org", "admin-id", 1);
        await CreateOrganizationWithDatasets("dest-org", "admin-id", 0);

        _datasetManagerMock.Setup(x => x.MoveToOrganization(
            "source-org",
            It.IsAny<string[]>(),
            "dest-org",
            It.IsAny<ConflictResolutionStrategy>()))
            .ReturnsAsync(new[]
            {
                new MoveDatasetResultDto { OriginalSlug = "dataset-0", NewSlug = "dataset-0", Success = true }
            });

        // Act
        var result = await _organizationsManager.Merge("source-org", "dest-org", deleteSourceOrganization: false);

        // Assert
        result.SourceOrganizationDeleted.ShouldBeFalse();

        // Verify the organization still exists
        var orgs = await _context.Organizations.ToListAsync();
        orgs.ShouldContain(o => o.Slug == "source-org");
    }

    [Test]
    public async Task Merge_EmptySourceOrganization_SucceedsWithNoDatasetsMoved()
    {
        // Arrange
        SetupAdminUser();
        await CreateOrganizationWithDatasets("source-org", "admin-id", 0);
        await CreateOrganizationWithDatasets("dest-org", "admin-id", 0);

        // Act
        var result = await _organizationsManager.Merge("source-org", "dest-org");

        // Assert
        result.DatasetsMovedCount.ShouldBe(0);
        result.DatasetsFailedCount.ShouldBe(0);
        result.DatasetResults.ShouldBeEmpty();
        result.SourceOrganizationDeleted.ShouldBeTrue();

        // MoveToOrganization should not be called for empty organization
        _datasetManagerMock.Verify(x => x.MoveToOrganization(
            It.IsAny<string>(),
            It.IsAny<string[]>(),
            It.IsAny<string>(),
            It.IsAny<ConflictResolutionStrategy>()), Times.Never);
    }

    [Test]
    public async Task Merge_TransfersUsersToDestinationOrganization()
    {
        // Arrange
        SetupAdminUser();
        var sourceOrg = await CreateOrganizationWithDatasets("source-org", "admin-id", 0);
        await CreateOrganizationWithDatasets("dest-org", "admin-id", 0);

        // Add users to source organization
        var sourceOrgEntity = await _context.Organizations
            .Include(o => o.Users)
            .FirstAsync(o => o.Slug == "source-org");

        sourceOrgEntity.Users = new List<OrganizationUser>
        {
            new OrganizationUser { UserId = "user-1", OrganizationSlug = "source-org" },
            new OrganizationUser { UserId = "user-2", OrganizationSlug = "source-org" }
        };
        await _context.SaveChangesAsync();

        // Act
        var result = await _organizationsManager.Merge("source-org", "dest-org");

        // Assert
        result.SourceOrganizationDeleted.ShouldBeTrue();

        // Verify users were transferred to destination organization
        var destOrgEntity = await _context.Organizations
            .Include(o => o.Users)
            .FirstAsync(o => o.Slug == "dest-org");

        destOrgEntity.Users.ShouldContain(u => u.UserId == "user-1");
        destOrgEntity.Users.ShouldContain(u => u.UserId == "user-2");
    }

    [Test]
    public async Task Merge_DoesNotDuplicateUsersInDestination()
    {
        // Arrange
        SetupAdminUser();
        await CreateOrganizationWithDatasets("source-org", "admin-id", 0);
        await CreateOrganizationWithDatasets("dest-org", "admin-id", 0);

        // Add user to both organizations
        var sourceOrgEntity = await _context.Organizations
            .Include(o => o.Users)
            .FirstAsync(o => o.Slug == "source-org");
        var destOrgEntity = await _context.Organizations
            .Include(o => o.Users)
            .FirstAsync(o => o.Slug == "dest-org");

        sourceOrgEntity.Users = new List<OrganizationUser>
        {
            new OrganizationUser { UserId = "shared-user", OrganizationSlug = "source-org" }
        };
        destOrgEntity.Users = new List<OrganizationUser>
        {
            new OrganizationUser { UserId = "shared-user", OrganizationSlug = "dest-org" }
        };
        await _context.SaveChangesAsync();

        // Act
        var result = await _organizationsManager.Merge("source-org", "dest-org");

        // Assert
        // Reload and verify no duplicates
        destOrgEntity = await _context.Organizations
            .Include(o => o.Users)
            .FirstAsync(o => o.Slug == "dest-org");

        destOrgEntity.Users.Count(u => u.UserId == "shared-user").ShouldBe(1);
    }

    [Test]
    public async Task Merge_SourceOrganizationNotFound_ThrowsNotFoundException()
    {
        // Arrange
        SetupAdminUser();
        await CreateOrganizationWithDatasets("dest-org", "admin-id", 0);

        // Act & Assert
        var action = () => _organizationsManager.Merge("non-existent-org", "dest-org");
        await Should.ThrowAsync<NotFoundException>(action);
    }

    [Test]
    public async Task Merge_DestinationOrganizationNotFound_ThrowsNotFoundException()
    {
        // Arrange
        SetupAdminUser();
        await CreateOrganizationWithDatasets("source-org", "admin-id", 0);

        // Act & Assert
        var action = () => _organizationsManager.Merge("source-org", "non-existent-org");
        await Should.ThrowAsync<NotFoundException>(action);
    }

    [Test]
    public async Task Merge_AllDatasetsFail_DoesNotDeleteSourceOrganization()
    {
        // Arrange
        SetupAdminUser();
        await CreateOrganizationWithDatasets("source-org", "admin-id", 2);
        await CreateOrganizationWithDatasets("dest-org", "admin-id", 0);

        _datasetManagerMock.Setup(x => x.MoveToOrganization(
            "source-org",
            It.IsAny<string[]>(),
            "dest-org",
            It.IsAny<ConflictResolutionStrategy>()))
            .ReturnsAsync(new[]
            {
                new MoveDatasetResultDto { OriginalSlug = "dataset-0", Success = false, Error = "Error 1" },
                new MoveDatasetResultDto { OriginalSlug = "dataset-1", Success = false, Error = "Error 2" }
            });

        // Act
        var result = await _organizationsManager.Merge("source-org", "dest-org");

        // Assert
        result.DatasetsMovedCount.ShouldBe(0);
        result.DatasetsFailedCount.ShouldBe(2);
        result.SourceOrganizationDeleted.ShouldBeFalse();

        // Verify source organization still exists
        var orgs = await _context.Organizations.ToListAsync();
        orgs.ShouldContain(o => o.Slug == "source-org");
    }

    #endregion

    #region ListPublic Tests

    [Test]
    public async Task ListPublic_ReturnsAllPublicOrganizations()
    {
        // Arrange
        SetupAdminUser();

        // Create a public organization
        var publicOrg = new Organization
        {
            Slug = "public-test-org",
            Name = "Public Test Organization",
            Description = "A public organization for testing",
            IsPublic = true,
            OwnerId = "admin-id",
            CreationDate = DateTime.Now
        };

        // Create a private organization
        var privateOrg = new Organization
        {
            Slug = "private-test-org",
            Name = "Private Test Organization",
            Description = "A private organization for testing",
            IsPublic = false,
            OwnerId = "admin-id",
            CreationDate = DateTime.Now
        };

        await _context.Organizations.AddRangeAsync(publicOrg, privateOrg);
        await _context.SaveChangesAsync();

        // Act
        var organizations = (await _organizationsManager.ListPublic()).ToArray();

        // Assert
        // Should include the default public org + our new public org
        organizations.Length.ShouldBe(2);
        organizations.ShouldContain(org => org.Slug == MagicStrings.PublicOrganizationSlug);
        organizations.ShouldContain(org => org.Slug == "public-test-org");
        organizations.ShouldNotContain(org => org.Slug == "private-test-org");
    }

    [Test]
    public async Task ListPublic_CanBeCalledWithoutAuthentication()
    {
        // Arrange - Setup anonymous user (simulating unauthenticated request)
        SetupAnonymousUser();

        // Act - Should not throw even without authentication
        var organizations = (await _organizationsManager.ListPublic()).ToArray();

        // Assert
        organizations.ShouldNotBeNull();
        organizations.Length.ShouldBe(1); // Only the default public organization
        organizations.First().Slug.ShouldBe(MagicStrings.PublicOrganizationSlug);
    }

    [Test]
    public async Task ListPublic_ResolvesOwnerNames()
    {
        // Arrange
        SetupAdminUser();

        // Create a public organization with an owner
        var publicOrg = new Organization
        {
            Slug = "public-test-org",
            Name = "Public Test Organization",
            Description = "A public organization for testing",
            IsPublic = true,
            OwnerId = "admin-user-id", // This exists in the test app context
            CreationDate = DateTime.Now
        };

        await _context.Organizations.AddAsync(publicOrg);
        await _context.SaveChangesAsync();

        // Act
        var organizations = (await _organizationsManager.ListPublic()).ToArray();

        // Assert
        var org = organizations.FirstOrDefault(o => o.Slug == "public-test-org");
        org.ShouldNotBeNull();
        org.Owner.ShouldBe("admin"); // The username from the app context
    }

    [Test]
    public async Task List_DoesNotIncludePublicOrganizationsOfOtherUsers()
    {
        // Arrange
        var standardUser = SetupStandardUser("standard-user");

        // Create a public organization owned by another user
        var otherUserPublicOrg = new Organization
        {
            Slug = "other-user-public-org",
            Name = "Other User Public Org",
            Description = "A public organization owned by another user",
            IsPublic = true,
            OwnerId = "different-user",
            CreationDate = DateTime.Now
        };

        await _context.Organizations.AddAsync(otherUserPublicOrg);
        await _context.SaveChangesAsync();

        // Act
        var organizations = (await _organizationsManager.List()).ToArray();

        // Assert
        // Should NOT include the other user's public organization
        // Should only include: the special 'public' org and user's own orgs
        organizations.ShouldContain(org => org.Slug == MagicStrings.PublicOrganizationSlug);
        organizations.ShouldNotContain(org => org.Slug == "other-user-public-org");
    }

    #endregion

    #region Additional Helper Methods

    private User SetupStandardUser(string userId)
    {
        var standardUser = new User
        {
            Id = userId,
            UserName = userId,
            Email = "standard@example.com"
        };

        _authManagerMock.Setup(x => x.GetCurrentUser()).ReturnsAsync(standardUser);
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(false);
        _authManagerMock.Setup(x => x.UserExists(It.IsAny<string>())).ReturnsAsync(true);
        _authManagerMock.Setup(x => x.CanListOrganizations(It.IsAny<User>())).ReturnsAsync(true);

        // Only allow access to owned organizations
        _authManagerMock.Setup(x => x.RequestAccess(It.IsAny<Organization>(), It.IsAny<AccessType>()))
            .ReturnsAsync((Organization org, AccessType _) => org.OwnerId == userId || org.IsPublic);

        return standardUser;
    }

    private void SetupAnonymousUser()
    {
        _authManagerMock.Setup(x => x.GetCurrentUser()).ReturnsAsync((User)null);
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(false);
        _authManagerMock.Setup(x => x.CanListOrganizations(It.IsAny<User>())).ReturnsAsync(false);
        _authManagerMock.Setup(x => x.RequestAccess(It.IsAny<Organization>(), It.IsAny<AccessType>()))
            .ReturnsAsync(false);
    }

    private async Task<OrganizationDto> CreateOrganizationForUser(string userId)
    {
        var org = new Organization
        {
            Slug = "test-org",
            Name = "Test Organization",
            Description = "Test Description",
            IsPublic = false,
            OwnerId = userId,
            CreationDate = DateTime.Now
        };

        await _context.Organizations.AddAsync(org);
        await _context.SaveChangesAsync();

        return org.ToDto();
    }

    private async Task<OrganizationDto> CreateOrganizationWithDatasets(string slug, string ownerId, int datasetCount)
    {
        var datasets = new List<Dataset>();
        for (var i = 0; i < datasetCount; i++)
        {
            datasets.Add(new Dataset
            {
                Slug = $"dataset-{i}",
                CreationDate = DateTime.Now,
                InternalRef = Guid.NewGuid()
            });
        }

        var org = new Organization
        {
            Slug = slug,
            Name = $"Organization {slug}",
            Description = $"Description for {slug}",
            IsPublic = false,
            OwnerId = ownerId,
            CreationDate = DateTime.Now,
            Datasets = datasets
        };

        await _context.Organizations.AddAsync(org);
        await _context.SaveChangesAsync();

        return org.ToDto();
    }

    #endregion

    #endregion

    #region Helper Methods

    private void SetupAdminUser()
    {
        var adminUser = new User
        {
            UserName = "admin",
            Email = "admin@example.com",
            Id = "admin-id"
        };

        _authManagerMock.Setup(x => x.GetCurrentUser()).ReturnsAsync(adminUser);
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);
        _authManagerMock.Setup(x => x.UserExists(It.IsAny<string>())).ReturnsAsync(true);
    }

    private void SetupDefaultAuthManagerBehavior()
    {
        _authManagerMock.Setup(x => x.CanListOrganizations(It.IsAny<User>())).ReturnsAsync(true);
        _authManagerMock.Setup(x => x.RequestAccess(It.IsAny<Organization>(), It.IsAny<AccessType>()))
            .ReturnsAsync(true);
    }

    private static OrganizationDto CreateTestOrganizationDto(string slug)
    {
        return new OrganizationDto
        {
            Slug = slug,
            Name = "Test Organization",
            Description = "Test Description",
            IsPublic = false,
            CreationDate = DateTime.Now
        };
    }

    private static void AssertPublicOrganization(OrganizationDto org)
    {
        org.Description.ShouldBe("Public organization");
        org.Slug.ShouldBe(MagicStrings.PublicOrganizationSlug);
        org.IsPublic.ShouldBeTrue();
        org.Owner.ShouldBeNull();
        org.Name.ShouldBe("Public");
    }

    private static async Task<RegistryContext> CreateTestRegistryContext()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(databaseName: $"RegistryDatabase-{Guid.NewGuid()}")
            .Options;

        var context = new RegistryContext(options);

        var publicOrg = new Organization
        {
            Slug = MagicStrings.PublicOrganizationSlug,
            Name = "Public",
            CreationDate = DateTime.Now,
            Description = "Public organization",
            IsPublic = true,
            OwnerId = null,
            Datasets = new List<Dataset>
            {
                new Dataset
                {
                    Slug = MagicStrings.DefaultDatasetSlug,
                    CreationDate = DateTime.Now,
                    InternalRef = Guid.Parse("0a223495-84a0-4c15-b425-c7ef88110e75")
                }
            }
        };

        await context.Organizations.AddAsync(publicOrg);
        await context.SaveChangesAsync();

        return context;
    }

    private static async Task<ApplicationDbContext> CreateTestApplicationContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: $"RegistryAppDatabase-{Guid.NewGuid()}")
            .Options;

        var context = new ApplicationDbContext(options);

        var adminRole = new IdentityRole
        {
            Id = "admin-role-id",
            Name = "admin",
            NormalizedName = "ADMIN"
        };

        var standardRole = new IdentityRole
        {
            Id = "standard-role-id",
            Name = "standard",
            NormalizedName = "STANDARD"
        };

        var adminUser = new User
        {
            Id = "admin-user-id",
            UserName = "admin",
            Email = "admin@example.com",
            NormalizedUserName = "ADMIN"
        };

        await context.Roles.AddRangeAsync(adminRole, standardRole);
        await context.Users.AddAsync(adminUser);
        await context.UserRoles.AddAsync(new IdentityUserRole<string>
        {
            RoleId = adminRole.Id,
            UserId = adminUser.Id
        });

        await context.SaveChangesAsync();

        return context;
    }

    #endregion
}