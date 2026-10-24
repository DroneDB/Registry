#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Ports.Import;
using Registry.Test.Common;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Import;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// What the two import managers do on the way to the <c>import-dataset</c> and <c>import-file</c> heavy tasks
/// (RQ-12 split their constructors; these pin the behaviour across it): access checks, the source allow-list,
/// the probe, the quota gate and budget, the encrypted credentials, the task submitted in the name of the
/// current user, and the rollback of the empty dataset when the submission fails.
/// </summary>
[TestFixture]
[Category(TestCategories.Unit)]
public class ImportManagersTests
{
    private const string Org = "acme";
    private const string Ds = "target";

    private Mock<IAuthManager> _auth = null!;
    private Mock<IDatasetAccessResolver> _access = null!;
    private Mock<IHeavyTaskRunner> _runner = null!;
    private Mock<IDatasetsManager> _datasetsManager = null!;
    private Mock<IDatasetResolver> _datasets = null!;
    private Mock<IUrlBuilder> _urls = null!;
    private Mock<IStorageQuota> _quota = null!;
    private Mock<IImportSource> _source = null!;
    private Mock<IImportSourceFactory> _factory = null!;
    private HeavyTaskSubmitRequest? _submitted;
    private ImportSettings _settings = null!;
    private ClaimsPrincipal _caller = null!;

    private sealed class ReversingProtector : IImportCredentialProtector
    {
        public string Protect(string plaintext) => new(plaintext.Reverse().ToArray());
        public string Unprotect(string ciphertext) => new(ciphertext.Reverse().ToArray());
    }

    [SetUp]
    public void SetUp()
    {
        _settings = new ImportSettings();
        _caller = new ClaimsPrincipal(new ClaimsIdentity("test"));

        _auth = new Mock<IAuthManager>();
        _auth.Setup(a => a.GetCurrentUser()).ReturnsAsync(new User { Id = "u1", UserName = "alice" });

        _access = new Mock<IDatasetAccessResolver>();
        _access.Setup(a => a.ResolveOrganizationAsync(Org, AccessType.Write, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Organization { Slug = Org });
        _access.Setup(a => a.ResolveAsync(Org, Ds, AccessType.Write, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatasetAccess(new Dataset { Slug = Ds, InternalRef = Guid.NewGuid() }, () => Mock.Of<IDDB>()));

        _submitted = null;
        _runner = new Mock<IHeavyTaskRunner>();
        _runner.Setup(r => r.SubmitAsync(It.IsAny<HeavyTaskSubmitRequest>(), It.IsAny<CancellationToken>()))
            .Callback<HeavyTaskSubmitRequest, CancellationToken>((r, _) => _submitted = r)
            .ReturnsAsync(new HeavyTaskSubmitResult("job-1", "import-dataset", "1", false, null));

        _datasetsManager = new Mock<IDatasetsManager>();
        _datasetsManager.Setup(m => m.AddNew(Org, It.IsAny<DatasetNewDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, DatasetNewDto d, CancellationToken _) => new DatasetDto { Slug = d.Slug });

        _datasets = new Mock<IDatasetResolver>();
        _datasets.Setup(d => d.GetDatasetAsync(Org, It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string slug, bool _, bool _, CancellationToken _) => new Dataset { Slug = slug });
        _urls = new Mock<IUrlBuilder>();
        _urls.Setup(u => u.GenerateDatasetUrl(It.IsAny<Dataset>(), It.IsAny<bool>())).Returns("https://hub/acme/new");

        _quota = new Mock<IStorageQuota>();
        _quota.Setup(q => q.GetUserStorage(It.IsAny<User>())).Returns(new UserStorageInfo { Total = null, Used = 0 });

        _source = new Mock<IImportSource>();
        _source.Setup(s => s.SourceType).Returns("archive-url");
        _source.Setup(s => s.ProbeAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImportSourceProbe(true, null, 2048, null, "My Dataset"));
        _factory = new Mock<IImportSourceFactory>();
        _factory.Setup(f => f.AvailableTypes).Returns(["archive-url", "registry"]);
        _factory.Setup(f => f.Resolve(It.IsAny<string>())).Returns(_source.Object);
    }

    private static IOptions<ImportSettings> Options(ImportSettings settings) => Microsoft.Extensions.Options.Options.Create(settings);

    private ICurrentUserTaskSubmitter Submitter() => new CurrentUserTaskSubmitter(_runner.Object, _auth.Object,
        new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = _caller } });

    private ImportManager CreateImportManager() => new(_access.Object,
        new ImportSourceCatalog(_factory.Object, Options(_settings)),
        new ImportQuotaGate(_auth.Object, _quota.Object),
        new ImportTargetDatasets(_datasetsManager.Object, _datasets.Object, _urls.Object, NullLogger<ImportTargetDatasets>.Instance),
        Submitter(), new ImportCredentials(new ReversingProtector()));

    private FileUrlImportManager CreateFileUrlManager() => new(_access.Object, Submitter(),
        new ImportCredentials(new ReversingProtector()), null!, Options(_settings), TestPaths.Policy);

    [Test]
    public async Task SourceTypes_AreTheAllowedOnes_AndNeedWriteAccessToTheOrganization()
    {
        _settings.AllowedSourceTypes = ["archive-url"];

        var types = await CreateImportManager().GetAvailableSourceTypes(Org);

        types.ShouldBe(["archive-url"]);
        _access.Verify(a => a.ResolveOrganizationAsync(Org, AccessType.Write, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Verify_ReturnsTheProbe_AndASlugOfTheSuggestedName()
    {
        var result = await CreateImportManager().VerifyAsync(Org, new VerifyImportRequestDto
        {
            SourceType = "archive-url",
            Params = new Dictionary<string, string> { ["url"] = "https://example.org/a.zip" }
        }, CancellationToken.None);

        result.Reachable.ShouldBeTrue();
        result.EstimatedBytes.ShouldBe(2048);
        result.SuggestedName.ShouldBe("My Dataset");
        result.SuggestedSlug.ShouldBe("my-dataset");
    }

    [TestCase("", "A source type is required.")]
    [TestCase("ftp", "Unknown import source type 'ftp'.")]
    public async Task Verify_RefusesAMissingOrUnknownSourceType(string sourceType, string message)
    {
        var ex = await Should.ThrowAsync<BadRequestException>(() => CreateImportManager().VerifyAsync(Org,
            new VerifyImportRequestDto { SourceType = sourceType }, CancellationToken.None));

        ex.Message.ShouldBe(message);
    }

    [Test]
    public async Task Verify_RefusesADisabledSourceType()
    {
        _settings.AllowedSourceTypes = ["registry"];

        var ex = await Should.ThrowAsync<BadRequestException>(() => CreateImportManager().VerifyAsync(Org,
            new VerifyImportRequestDto { SourceType = "archive-url" }, CancellationToken.None));

        ex.Message.ShouldBe("The import source type 'archive-url' is disabled on this server.");
    }

    [Test]
    public async Task Create_MakesTheDataset_AndSubmitsTheTaskOfTheCurrentUser_WithEncryptedPasswordAndTheBudget()
    {
        _quota.Setup(q => q.GetUserStorage(It.IsAny<User>())).Returns(new UserStorageInfo { Total = 10_000, Used = 4_000 });

        var result = await CreateImportManager().CreateAsync(Org, new CreateImportRequestDto
        {
            SourceType = "archive-url",
            Params = new Dictionary<string, string>
            {
                ["url"] = "https://example.org/a.zip", ["username"] = "bob", ["password"] = "secret"
            }
        }, CancellationToken.None);

        result.TaskId.ShouldBe("job-1");
        result.OrgSlug.ShouldBe(Org);
        result.DsSlug.ShouldBe("my-dataset");
        result.DatasetUrl.ShouldBe("https://hub/acme/new");
        _quota.Verify(q => q.CheckCurrentUserStorage(2048), Times.Once);
        _datasetsManager.Verify(m => m.AddNew(Org, It.Is<DatasetNewDto>(d =>
            d.Slug == "my-dataset" && d.Name == "My Dataset" && d.Visibility == Visibility.Private), It.IsAny<CancellationToken>()), Times.Once);

        var request = _submitted.ShouldNotBeNull();
        request.OrgSlug.ShouldBe(Org);
        request.DsSlug.ShouldBe("my-dataset");
        request.ToolId.ShouldBe("import-dataset");
        request.Version.ShouldBe("1");
        request.Force.ShouldBeFalse();
        request.UserId.ShouldBe("u1");
        request.Caller.ShouldBeSameAs(_caller);

        request.Params.GetProperty("sourceType").GetString().ShouldBe("archive-url");
        request.Params.GetProperty("budgetBytes").GetInt64().ShouldBe(6_000);
        var inner = request.Params.GetProperty("params");
        inner.GetProperty("url").GetString().ShouldBe("https://example.org/a.zip");
        inner.GetProperty("username").GetString().ShouldBe("bob");
        inner.GetProperty("password").GetString().ShouldBe("ENC:" + "terces");
    }

    [Test]
    public async Task Create_UsesTheNameAndSlugOfTheRequest_AndHasNoBudgetWithoutALimit()
    {
        _quota.Setup(q => q.GetUserStorage(It.IsAny<User>())).Returns(new UserStorageInfo { Total = null, Used = 4_000 });

        await CreateImportManager().CreateAsync(Org, new CreateImportRequestDto
        {
            SourceType = "archive-url", Name = "Chosen", Slug = "chosen-slug", Visibility = Visibility.Public
        }, CancellationToken.None);

        _datasetsManager.Verify(m => m.AddNew(Org, It.Is<DatasetNewDto>(d =>
            d.Slug == "chosen-slug" && d.Name == "Chosen" && d.Visibility == Visibility.Public), It.IsAny<CancellationToken>()), Times.Once);
        _submitted!.Params.TryGetProperty("budgetBytes", out _).ShouldBeFalse();
    }

    [Test]
    public async Task Create_RefusesAnUnreachableSource_BeforeAnythingIsCreated()
    {
        _source.Setup(s => s.ProbeAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImportSourceProbe(false, "no route", null, null, null));

        var ex = await Should.ThrowAsync<BadRequestException>(() => CreateImportManager().CreateAsync(Org,
            new CreateImportRequestDto { SourceType = "archive-url" }, CancellationToken.None));

        ex.Message.ShouldBe("no route");
        _datasetsManager.Verify(m => m.AddNew(It.IsAny<string>(), It.IsAny<DatasetNewDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _runner.Verify(r => r.SubmitAsync(It.IsAny<HeavyTaskSubmitRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Create_RollsTheEmptyDatasetBack_WhenTheSubmissionFails()
    {
        var failure = new InvalidOperationException("queue is down");
        _runner.Setup(r => r.SubmitAsync(It.IsAny<HeavyTaskSubmitRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => CreateImportManager().CreateAsync(Org,
            new CreateImportRequestDto { SourceType = "archive-url" }, CancellationToken.None));

        ex.ShouldBeSameAs(failure);
        _datasetsManager.Verify(m => m.Delete(Org, "my-dataset", CancellationToken.None), Times.Once);
    }

    [Test]
    public async Task Create_StillAnswers_WhenTheDatasetUrlCannotBeBuilt()
    {
        _urls.Setup(u => u.GenerateDatasetUrl(It.IsAny<Dataset>(), It.IsAny<bool>())).Throws(new InvalidOperationException("no host"));

        var result = await CreateImportManager().CreateAsync(Org,
            new CreateImportRequestDto { SourceType = "archive-url" }, CancellationToken.None);

        result.TaskId.ShouldBe("job-1");
        result.DatasetUrl.ShouldBeNull();
        _datasetsManager.Verify(m => m.Delete(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Browse_ListsTheRemoteItems_OfABrowsableSource()
    {
        var browsable = new Mock<IImportSource>();
        browsable.As<IBrowsableImportSource>().Setup(b => b.BrowseOrganizationsAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new RemoteBrowseItem("o1", "Org One")]);
        browsable.As<IBrowsableImportSource>().Setup(b => b.BrowseDatasetsAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new RemoteBrowseItem("d1", "Data One"), new RemoteBrowseItem("d2", "Data Two")]);
        _factory.Setup(f => f.Resolve("registry")).Returns(browsable.Object);

        var orgs = await CreateImportManager().BrowseAsync(Org,
            new BrowseImportRequestDto { SourceType = "registry", BrowseType = "organizations" }, CancellationToken.None);
        var datasets = await CreateImportManager().BrowseAsync(Org,
            new BrowseImportRequestDto { SourceType = "registry", BrowseType = "datasets" }, CancellationToken.None);

        orgs.Items.Select(i => (i.Slug, i.Name)).ShouldBe([("o1", "Org One")]);
        datasets.Items.Select(i => i.Slug).ShouldBe(["d1", "d2"]);
    }

    [Test]
    public async Task Browse_RefusesASourceThatCannotBrowse_AndAnUnknownBrowseType()
    {
        var notBrowsable = await Should.ThrowAsync<BadRequestException>(() => CreateImportManager().BrowseAsync(Org,
            new BrowseImportRequestDto { SourceType = "archive-url", BrowseType = "datasets" }, CancellationToken.None));
        notBrowsable.Message.ShouldBe("The 'archive-url' source does not support browsing.");

        var browsable = new Mock<IImportSource>();
        browsable.As<IBrowsableImportSource>();
        _factory.Setup(f => f.Resolve("registry")).Returns(browsable.Object);
        var unknown = await Should.ThrowAsync<BadRequestException>(() => CreateImportManager().BrowseAsync(Org,
            new BrowseImportRequestDto { SourceType = "registry", BrowseType = "files" }, CancellationToken.None));
        unknown.Message.ShouldBe("Unknown browse type 'files'. Use 'organizations' or 'datasets'.");
    }

    [Test]
    public async Task FileImport_SubmitsTheTaskOfTheCurrentUser_WithTheSanitizedNameAndTheEncryptedPassword()
    {
        _runner.Setup(r => r.SubmitAsync(It.IsAny<HeavyTaskSubmitRequest>(), It.IsAny<CancellationToken>()))
            .Callback<HeavyTaskSubmitRequest, CancellationToken>((r, _) => _submitted = r)
            .ReturnsAsync(new HeavyTaskSubmitResult("job-2", "import-file", "1", false, null));

        var result = await CreateFileUrlManager().ImportAsync(Org, Ds, new UrlImportRequestDto
        {
            Url = "https://example.org/files/orthophoto.tif", Folder = "maps", Username = "bob", Password = "secret",
            Overwrite = true, SizeBytes = 123
        }, CancellationToken.None);

        result.TaskId.ShouldBe("job-2");
        var request = _submitted.ShouldNotBeNull();
        request.ToolId.ShouldBe("import-file");
        request.DsSlug.ShouldBe(Ds);
        request.Path.ShouldBe("orthophoto.tif");
        request.UserId.ShouldBe("u1");
        request.Caller.ShouldBeSameAs(_caller);
        request.Params.GetProperty("url").GetString().ShouldBe("https://example.org/files/orthophoto.tif");
        request.Params.GetProperty("fileName").GetString().ShouldBe("orthophoto.tif");
        request.Params.GetProperty("folder").GetString().ShouldBe("maps");
        request.Params.GetProperty("overwrite").GetBoolean().ShouldBeTrue();
        request.Params.GetProperty("username").GetString().ShouldBe("bob");
        request.Params.GetProperty("password").GetString().ShouldBe("ENC:terces");
        request.Params.GetProperty("sizeBytes").GetInt64().ShouldBe(123);
    }

    [Test]
    public async Task FileImport_SendsNoPassword_WhenThereIsNone()
    {
        await CreateFileUrlManager().ImportAsync(Org, Ds, new UrlImportRequestDto
        {
            Url = "https://example.org/a.tif", FileName = "renamed.tif"
        }, CancellationToken.None);

        _submitted!.Params.GetProperty("password").GetString().ShouldBe(string.Empty);
        _submitted.Params.GetProperty("fileName").GetString().ShouldBe("renamed.tif");
        _submitted.Params.TryGetProperty("sizeBytes", out _).ShouldBeFalse();
    }

    [Test]
    public async Task FileImport_RefusesABlockedExtension_BeforeSubmitting()
    {
        _settings.BlockedFileExtensions = [".exe"];

        var ex = await Should.ThrowAsync<BadRequestException>(() => CreateFileUrlManager().ImportAsync(Org, Ds,
            new UrlImportRequestDto { Url = "https://example.org/setup.exe" }, CancellationToken.None));

        ex.Message.ShouldBe("Files of this type are not allowed: 'setup.exe'.");
        _runner.Verify(r => r.SubmitAsync(It.IsAny<HeavyTaskSubmitRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
