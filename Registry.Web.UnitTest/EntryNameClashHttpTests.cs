using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Services.Managers;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-EXP-04, FR-EXP-15, D-040 through HTTP: a rename, a move or a new folder whose name an entry of any type already
/// holds (letter case ignored) answers 409 with the one "already exists" message and <c>conflictPath</c>, before the
/// file system or the index is touched; a free name still works, and so does a rename that only changes the case.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~EntryNameClashHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class EntryNameClashHttpTests : IDisposable
{
    private const string Owner = "entryclash-owner";
    private const string Org = "entryclash-org";
    private const string Ds = "entryclash-ds";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private string _folder = null!;
    private readonly Dictionary<string, EntryType> _index = [];
    private readonly List<string> _moves = [];
    private readonly List<string> _adds = [];

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        // A dataset without visibility metadata is private (native ddb throws on a missing key)
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
        // The index of the dataset: a path to its type, in the case it was added with
        _factory.DdbWrapper.Setup(d => d.GetEntry(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string _, string path) => _index.TryGetValue(path, out var type)
                ? new Entry { Path = path, Type = type, Hash = "h", Size = 3, ModifiedTime = DateTime.UtcNow }
                : null);
        _factory.DdbWrapper.Setup(d => d.ListEntrySummaries(It.IsAny<string>(), It.Is<EntryQuery>(q => !q.Recursive)))
            .Returns((string _, EntryQuery q) => _index
                .Where(e => string.Equals(Parent(e.Key), q.Path ?? string.Empty, StringComparison.Ordinal))
                .Select(e => new EntrySummary { Path = e.Key, Hash = "h", Type = e.Value }).ToList());
        _factory.DdbWrapper.Setup(d => d.MoveEntry(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback((string _, string source, string dest) =>
            {
                _moves.Add($"{source}>{dest}");
                var type = _index[source];
                _index.Remove(source);
                _index[dest] = type;
            });
        _factory.DdbWrapper.Setup(d => d.Add(It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<bool>()))
            .Callback((string _, string[] paths, bool _) =>
            {
                foreach (var path in paths)
                {
                    var relative = Path.GetRelativePath(_folder, path).Replace('\\', '/');
                    _adds.Add(relative);
                    _index[relative] = EntryType.Directory;
                }
            })
            .Returns([]);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(Owner);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: false);
        var ds = Seed.Dataset(context, org, Ds);
        await context.SaveChangesAsync();

        _folder = scope.ServiceProvider.GetRequiredService<IDdbManager>().Get(Org, ds.InternalRef).DatasetFolderPath;
    }

    [SetUp]
    public void SetUp()
    {
        _index.Clear();
        _moves.Clear();
        _adds.Clear();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task Rename_ToASiblingFile_Is409WithTheMessageAndConflictPath_AndChangesNothing()
    {
        File("README.md", "readme");
        File("odm_orthophoto.tif", "ortho");

        var (status, body) = await PutAsync("README.md", "odm_orthophoto.tif");

        status.ShouldBe(HttpStatusCode.Conflict);
        body["detail"]!.GetValue<string>().ShouldBe("A file or folder named 'odm_orthophoto.tif' already exists in this folder");
        body["conflictPath"]!.GetValue<string>().ShouldBe("odm_orthophoto.tif");
        _moves.ShouldBeEmpty();
        Read("README.md").ShouldBe("readme");
        Read("odm_orthophoto.tif").ShouldBe("ortho");
    }

    [Test]
    public async Task Rename_ToASiblingThatDiffersOnlyInCase_Is409NamingTheEntryAsItIsStored()
    {
        File("README.md", "readme");
        File("Photo.JPG", "photo");

        var (status, body) = await PutAsync("README.md", "photo.jpg");

        status.ShouldBe(HttpStatusCode.Conflict);
        body["conflictPath"]!.GetValue<string>().ShouldBe("Photo.JPG");
        body["detail"]!.GetValue<string>().ShouldBe("A file or folder named 'Photo.JPG' already exists in this folder");
        _moves.ShouldBeEmpty();
    }

    [Test]
    public async Task Move_OfAFolderOntoAFile_AndOfAFileOntoAFolder_AreBoth409()
    {
        Folder("sub");
        File("sub/inner.txt", "inner");
        File("taken", "file named like a folder");
        File("a.txt", "a");
        Folder("target");

        (await PutAsync("sub", "taken")).Status.ShouldBe(HttpStatusCode.Conflict);
        (await PutAsync("a.txt", "target")).Status.ShouldBe(HttpStatusCode.Conflict);
        _moves.ShouldBeEmpty();
    }

    [Test]
    public async Task Move_ToAFreeName_Works()
    {
        File("a.txt", "a");
        Folder("folder");

        var (status, body) = await PutAsync("a.txt", "folder/a.txt");

        status.ShouldBe(HttpStatusCode.OK);
        body["path"]!.GetValue<string>().ShouldBe("folder/a.txt");
        _moves.ShouldBe(["a.txt>folder/a.txt"]);
    }

    // D-040: the folders of the destination are found without regard to case, and written in the case they have
    [Test]
    public async Task Move_IntoAFolderNamedInAnotherCase_LandsInThatFolder_WithoutASiblingFolder()
    {
        File("b.txt", "b");
        Folder("Album");

        var (status, body) = await PutAsync("b.txt", "ALBUM/b.txt");

        status.ShouldBe(HttpStatusCode.OK);
        body["path"]!.GetValue<string>().ShouldBe("Album/b.txt");
        _moves.ShouldBe(["b.txt>Album/b.txt"]);
        Read("Album/b.txt").ShouldBe("b");
        Directory.GetDirectories(_folder).Select(Path.GetFileName).ShouldNotContain("ALBUM");
    }

    // "archive/x" is inside the folder "Archive" (names compare without regard to case): refused as a move into
    // itself, before anything is touched (not a failure of the file system)
    [Test]
    public async Task Move_OfAFolderIntoItselfUnderAnotherCase_Is400()
    {
        Folder("Archive");
        File("Archive/inner.txt", "inner");

        var (status, body) = await PutAsync("Archive", "archive/x");

        status.ShouldBe(HttpStatusCode.BadRequest);
        body["detail"]!.GetValue<string>().ShouldBe("Cannot move a path onto itself or one of its descendants");
        _moves.ShouldBeEmpty();
        Read("Archive/inner.txt").ShouldBe("inner");
    }

    [Test]
    public async Task Rename_ThatOnlyChangesTheLetterCaseOfTheEntry_IsNotAClash()
    {
        File("report.PDF", "report");

        var (status, body) = await PutAsync("report.PDF", "Report.pdf");

        status.ShouldBe(HttpStatusCode.OK);
        body["path"]!.GetValue<string>().ShouldBe("Report.pdf");
        _moves.ShouldBe(["report.PDF>Report.pdf"]);
    }

    [Test]
    public async Task CreateFolder_WithTheNameOfAFile_OrOfAFolderInAnotherCase_Is409WithConflictPath()
    {
        File("data.csv", "csv");
        Folder("Images");

        var (file, fileBody) = await PostFolderAsync("data.csv");
        var (folder, folderBody) = await PostFolderAsync("images");

        file.ShouldBe(HttpStatusCode.Conflict);
        fileBody["detail"]!.GetValue<string>().ShouldBe("A file or folder named 'data.csv' already exists in this folder");
        fileBody["conflictPath"]!.GetValue<string>().ShouldBe("data.csv");
        folder.ShouldBe(HttpStatusCode.Conflict);
        folderBody["conflictPath"]!.GetValue<string>().ShouldBe("Images");
        folderBody["detail"]!.GetValue<string>().ShouldBe("A file or folder named 'Images' already exists in this folder");
        _adds.ShouldBeEmpty();
    }

    [Test]
    public async Task CreateFolder_WithAFreeName_Works()
    {
        File("data.csv", "csv");

        var (status, body) = await PostFolderAsync("fresh");

        status.ShouldBe(HttpStatusCode.Created);
        body["path"]!.GetValue<string>().ShouldBe("fresh");
    }

    // A file the index lists and the disk holds
    private void File(string path, string text)
    {
        var local = Path.Combine(_folder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        System.IO.File.WriteAllText(local, text);
        _index[path] = EntryType.Generic;
    }

    private void Folder(string path)
    {
        Directory.CreateDirectory(Path.Combine(_folder, path));
        _index[path] = EntryType.Directory;
    }

    private string Read(string path) => System.IO.File.ReadAllText(Path.Combine(_folder, path));

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private async Task<(HttpStatusCode Status, JsonObject Body)> PutAsync(string source, string dest)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["source"] = source, ["dest"] = dest });
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/orgs/{Org}/ds/{Ds}/obj") { Content = content }.AsUser(Owner);
        return await ReadAsync(request);
    }

    private async Task<(HttpStatusCode Status, JsonObject Body)> PostFolderAsync(string path)
    {
        using var content = new MultipartFormDataContent { { new StringContent(path), "path" } };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/orgs/{Org}/ds/{Ds}/obj") { Content = content }.AsUser(Owner);
        return await ReadAsync(request);
    }

    private async Task<(HttpStatusCode Status, JsonObject Body)> ReadAsync(HttpRequestMessage request)
    {
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonNode.Parse(text)!.AsObject());
    }
}
