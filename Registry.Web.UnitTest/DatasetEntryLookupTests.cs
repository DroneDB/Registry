using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The one "does this name already exist" lookup of what the Registry writes into a dataset
/// (<see cref="DatasetEntryLookup"/>, D-040): names match without regard to letter case, an unindexed file on disk
/// counts, and a refusal is a <see cref="NameClashException"/> whose 409 body carries the clash message of
/// <see cref="InputRules.NameClashMessage"/> and the <c>conflictPath</c> member.
/// </summary>
[TestFixture]
public class DatasetEntryLookupTests
{
    private string _folder = null!;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "entry-lookup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, true);
    }

    // A dataset whose index lists the given entries (folders end with '/')
    private Mock<IDDB> Dataset(params string[] entries)
    {
        var all = entries.Select(e => new EntrySummary
        {
            Path = e.TrimEnd('/'),
            Type = e.EndsWith('/') ? EntryType.Directory : EntryType.GeoRaster
        }).ToList();

        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.EntryExists(It.IsAny<string>())).Returns<string>(p => all.Any(e => e.Path == p));
        ddb.Setup(d => d.GetEntry(It.IsAny<string>())).Returns<string>(p =>
            all.FirstOrDefault(e => e.Path == p) is { } e ? new Entry { Path = e.Path, Type = e.Type } : null);
        ddb.Setup(d => d.FindSummaries(It.IsAny<EntryQuery>())).Returns<EntryQuery>(q =>
            all.Where(e => Parent(e.Path) == (q.Path ?? string.Empty)).ToList());
        ddb.Setup(d => d.GetLocalPath(It.IsAny<string>()))
            .Returns<string>(p => Path.Combine(_folder, p.Replace('/', Path.DirectorySeparatorChar)));
        return ddb;
    }

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    [Test]
    public void FreeName_IsNotFound()
    {
        DatasetEntryLookup.FindExisting(Dataset("a.tif").Object, "b.tif").ShouldBeNull();
        DatasetEntryLookup.ResolveOutput(Dataset("a.tif").Object, "dir/b.tif", overwrite: false).ShouldBe("dir/b.tif");
    }

    [Test]
    public void ExactName_IsFound()
    {
        var found = DatasetEntryLookup.FindExisting(Dataset("dir/", "dir/a.tif").Object, "dir/a.tif");

        found.ShouldBe(new ExistingEntry("dir/a.tif", false));
    }

    [Test]
    public void NameInAnotherLetterCase_IsFound_WithItsOwnPath()
    {
        var found = DatasetEntryLookup.FindExisting(Dataset("Images/", "Images/IMG_1.TIF").Object, "images/img_1.tif");

        found.ShouldBe(new ExistingEntry("Images/IMG_1.TIF", false));
    }

    [Test]
    public void ExactMatch_WinsOverACaseVariant()
    {
        var found = DatasetEntryLookup.FindExisting(Dataset("A.tif", "a.tif").Object, "a.tif");

        found!.Path.ShouldBe("a.tif");
    }

    [Test]
    public void Folder_IsFoundAsAFolder()
    {
        DatasetEntryLookup.FindExisting(Dataset("Photos/").Object, "photos")
            .ShouldBe(new ExistingEntry("Photos", true));
    }

    [Test]
    public void FileOnDiskThatTheIndexDoesNotList_IsFound()
    {
        File.WriteAllText(Path.Combine(_folder, "loose.tif"), "x");

        DatasetEntryLookup.FindExisting(Dataset().Object, "loose.tif").ShouldBe(new ExistingEntry("loose.tif", false));
    }

    [Test]
    public void ExistingName_WithoutOverwrite_IsANameClash()
    {
        var ex = Should.Throw<NameClashException>(() =>
            DatasetEntryLookup.ResolveOutput(Dataset("out/", "out/Merged.tif").Object, "out/merged.tif", false));

        ex.ConflictPath.ShouldBe("out/Merged.tif");
        ex.Message.ShouldBe("A file or folder named 'Merged.tif' already exists in this folder");
        ex.Message.ShouldBe(InputRules.NameClashMessage("Merged.tif"));
        ex.StatusCode.ShouldBe(409);
        ex.NoRetry.ShouldBeTrue();
    }

    [Test]
    public void ExistingName_WithOverwrite_IsWrittenAtTheExistingPath()
    {
        DatasetEntryLookup.ResolveOutput(Dataset("out/", "out/Merged.tif").Object, "OUT/merged.TIF", true)
            .ShouldBe("out/Merged.tif");
    }

    [Test]
    public void FreeName_InAFolderOfAnotherLetterCase_IsPlacedInTheExistingFolder()
    {
        var ddb = Dataset("out/", "out/a.tif").Object;

        DatasetEntryLookup.ResolveOutput(ddb, "Out/new.tif", overwrite: false).ShouldBe("out/new.tif");
        DatasetEntryLookup.ResolveOutput(ddb, "OUT/sub/new.tif", overwrite: false).ShouldBe("out/sub/new.tif");
        DatasetEntryLookup.Locate(ddb, "Out/new.tif").Existing.ShouldBeNull();
        DatasetEntryLookup.ResolveOutput(ddb, "New/x.tif", overwrite: false).ShouldBe("New/x.tif");
    }

    [Test]
    public void AFolder_IsNeverReplacedByAFile()
    {
        Should.Throw<NameClashException>(() =>
            DatasetEntryLookup.ResolveOutput(Dataset("out/").Object, "out", true)).ConflictPath.ShouldBe("out");
    }

    [Test]
    public void ApiErrorResponder_WritesTheConflictPathMember()
    {
        var problems = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider()
            .GetRequiredService<ProblemDetailsFactory>();
        var http = new DefaultHttpContext();
        http.Request.Path = "/orgs/org/ds/ds/tasks";

        var (descriptor, problem) = new ApiErrorResponder(problems, new CapturingLogger<ApiErrorResponder>())
            .Describe(http, new NameClashException("out/Merged.tif"));

        descriptor.StatusCode.ShouldBe(409);
        problem.ShouldNotBeNull();
        problem.Status.ShouldBe(409);
        problem.Detail.ShouldBe("A file or folder named 'Merged.tif' already exists in this folder");
        problem.Extensions[ApiErrorResponder.ConflictPathMember].ShouldBe("out/Merged.tif");
        problem.Extensions[ApiErrorResponder.NoRetryMember].ShouldBe(true);
    }

    [Test]
    public void ApiErrorResponder_OtherConflicts_HaveNoConflictPath()
    {
        var problems = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider()
            .GetRequiredService<ProblemDetailsFactory>();

        var (_, problem) = new ApiErrorResponder(problems, new CapturingLogger<ApiErrorResponder>())
            .Describe(new DefaultHttpContext(), new ConflictException("busy"));

        problem!.Extensions.ContainsKey(ApiErrorResponder.ConflictPathMember).ShouldBeFalse();
    }
}
