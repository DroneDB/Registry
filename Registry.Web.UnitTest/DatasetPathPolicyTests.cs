using System.IO;
using System.Linq;
using NUnit.Framework;
using Registry.Adapters.Testing;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <see cref="DatasetPathPolicy"/>: libddb's path rule (the golden vectors) with
/// Registry's two additions (".uploads" reserved as the first segment, a whitespace-only path is
/// empty), and the security cases of every entry point: NUL, traversal, absolute (POSIX, Windows
/// root, drive, UNC, device), reserved, zip-slip.
/// </summary>
[TestFixture]
public class DatasetPathPolicyTests
{
    private static readonly DatasetPathPolicy Policy = TestPaths.Policy;

    private static PathVector[] Vectors() => [.. PathVectors.All];

    // The vectors of the rule the policy applies: its reserved list is [".uploads"], so a vector
    // recorded without that list expects "reserved" for a ".uploads" first segment; and Registry
    // calls a whitespace-only path empty (libddb accepts " " as a name)
    [TestCaseSource(nameof(Vectors))]
    public void Check_AgreesWithTheVector_PlusRegistrysTwoAdditions(PathVector vector)
    {
        var expected = vector.Expected;
        if (vector.Input.Length > 0 && string.IsNullOrWhiteSpace(vector.Input))
            expected = NormalizedEntryPath.Rejection(EntryPathRejection.Empty);
        else if (vector.Reserved == null && expected.Path?.Split('/')[0] == ".uploads")
            expected = NormalizedEntryPath.Rejection(EntryPathRejection.Reserved);

        Policy.Check(vector.Input).ShouldBe(expected);
    }

    [TestCase("./a", "a")]                 // was rejected ("." segment): libddb drops it
    [TestCase("a/./b", "a/b")]             // idem
    [TestCase("a//b/", "a/b")]
    [TestCase(@"a\b", "a/b")]
    [TestCase("a..b.jpg", "a..b.jpg")]     // was rejected as a Transfer destination (Contains(".."))
    [TestCase("ab:c", "ab:c")]
    [TestCase(".ddbx/a.txt", ".ddbx/a.txt")]
    [TestCase("photos/.uploads/x", "photos/.uploads/x")]
    [TestCase("fotos/Überflug/été.jpg", "fotos/Überflug/été.jpg")]
    public void Normalize_Accepted_ReturnsLibddbsSpelling(string path, string expected) =>
        Policy.Normalize(path).ShouldBe(expected);

    [TestCase("../a", EntryPathRejection.Traversal)]
    [TestCase("a/../../b", EntryPathRejection.Traversal)]
    [TestCase(@"a\..\..\b", EntryPathRejection.Traversal)]
    [TestCase("a/..", EntryPathRejection.Traversal)]
    [TestCase("/etc/passwd", EntryPathRejection.Absolute)]
    [TestCase(@"\abs", EntryPathRejection.Absolute)]
    [TestCase(@"C:\x", EntryPathRejection.Absolute)]       // was accepted on Linux (Path.IsPathRooted)
    [TestCase("c:/x", EntryPathRejection.Absolute)]        // idem
    [TestCase("C:x", EntryPathRejection.Absolute)]         // idem
    [TestCase("C:", EntryPathRejection.Absolute)]          // idem
    [TestCase(@"\\server\share", EntryPathRejection.Absolute)]
    [TestCase("//server/share", EntryPathRejection.Absolute)]
    [TestCase(@"\\?\C:\x", EntryPathRejection.Absolute)]
    [TestCase("a\0b", EntryPathRejection.Nul)]
    [TestCase("\0", EntryPathRejection.Nul)]
    [TestCase(".ddb", EntryPathRejection.Reserved)]
    [TestCase(".ddb/x", EntryPathRejection.Reserved)]
    [TestCase("sub/.ddb/x", EntryPathRejection.Reserved)]
    [TestCase(@"a\.ddb\x", EntryPathRejection.Reserved)]
    [TestCase(".DDB/x", EntryPathRejection.Reserved)]          // a case-insensitive file system opens it as .ddb
    [TestCase("sub/.Ddb/x", EntryPathRejection.Reserved)]      // idem
    [TestCase(".ddb./x", EntryPathRejection.Reserved)]         // Win32 drops trailing dots and spaces
    [TestCase(".ddb /x", EntryPathRejection.Reserved)]         // idem
    [TestCase(".ddb::$INDEX_ALLOCATION/x", EntryPathRejection.Reserved)] // an NTFS stream of the folder
    [TestCase("DDB~1/x", EntryPathRejection.Reserved)]         // the NTFS short name of .ddb
    [TestCase("a/ddb~12/x", EntryPathRejection.Reserved)]      // idem
    [TestCase(".uploads", EntryPathRejection.Reserved)]
    [TestCase(".uploads/part.tmp", EntryPathRejection.Reserved)]
    [TestCase(@".uploads\part.tmp", EntryPathRejection.Reserved)]
    [TestCase("./.uploads/x", EntryPathRejection.Reserved)]
    [TestCase("", EntryPathRejection.Empty)]
    [TestCase("   ", EntryPathRejection.Empty)]
    [TestCase(".", EntryPathRejection.Empty)]
    [TestCase(null, EntryPathRejection.Empty)]
    public void Normalize_Rejected_Is400WithTheRule(string? path, string rejection)
    {
        var ex = Should.Throw<InvalidDatasetPathException>(() => Policy.Normalize(path));
        ex.Rejection.ShouldBe(rejection);
        ex.StatusCode.ShouldBe(400);
        ex.NoRetry.ShouldBeTrue();
        ex.Message.ShouldNotContain("\0");
    }

    // A lone surrogate (the UTF-8 marshaller would send U+FFFD, another path); built at run time:
    // an attribute argument is stored as UTF-8, which cannot hold one
    [TestCase(0xD800)]
    [TestCase(0xDC00)]
    public void Normalize_LoneSurrogate_IsEncoding(int surrogate) =>
        Should.Throw<InvalidDatasetPathException>(() => Policy.Normalize("a" + (char)surrogate + "b"))
            .Rejection.ShouldBe(EntryPathRejection.Encoding);

    [Test]
    public void Normalize_Reserved_SaysReservedPath() =>
        Should.Throw<InvalidDatasetPathException>(() => Policy.Normalize(".ddb/somefile.txt"))
            .Message.ShouldContain("reserved path");

    [TestCase(".ddb/x")]
    [TestCase("sub/.ddb/x")]
    [TestCase(".uploads/x")]
    [TestCase(".uploads")]
    [TestCase(".DDB/x")]
    [TestCase("DDB~1/x")]
    public void IsReserved_True(string path) => Policy.IsReserved(path).ShouldBeTrue();

    [TestCase(".ddbx/a.txt")]
    [TestCase("sub/.uploads/x")]
    [TestCase("x.ddb")]
    [TestCase("ddb~/x")]                // no number: not a short name
    [TestCase("ddb~1a/x")]              // idem
    [TestCase(".uploads.d/x")]
    [TestCase("a.txt")]
    [TestCase("")]
    [TestCase("/.ddb/x")] // absolute, not reserved: a received path is refused by Normalize either way
    public void IsReserved_False(string path) => Policy.IsReserved(path).ShouldBeFalse();

    [TestCase("/img.jpg", "img.jpg")]
    [TestCase("img.jpg", "img.jpg")]
    [TestCase("/sub/./img.jpg", "sub/img.jpg")]
    public void NormalizeRequestPath_DropsOneLeadingSlash(string path, string expected) =>
        Policy.NormalizeRequestPath(path).ShouldBe(expected);

    [TestCase("//img.jpg", EntryPathRejection.Absolute)]
    [TestCase("/../img.jpg", EntryPathRejection.Traversal)]
    [TestCase("/.uploads/x", EntryPathRejection.Reserved)]
    public void NormalizeRequestPath_Rejected(string path, string rejection) =>
        Should.Throw<InvalidDatasetPathException>(() => Policy.NormalizeRequestPath(path)).Rejection.ShouldBe(rejection);

    [TestCase(null, "")]
    [TestCase("", "")]
    [TestCase("  ", "")]
    [TestCase("/", "")]
    [TestCase(" /photos/ ", "photos")]
    [TestCase(@"photos\2024", "photos/2024")]
    public void NormalizeFolder_BlankIsTheRoot(string? folder, string expected) =>
        Policy.NormalizeFolder(folder).ShouldBe(expected);

    [TestCase(".uploads", EntryPathRejection.Reserved)]
    [TestCase("a/../..", EntryPathRejection.Traversal)]
    [TestCase("//server/share", EntryPathRejection.Absolute)]
    public void NormalizeFolder_Rejected(string folder, string rejection) =>
        Should.Throw<InvalidDatasetPathException>(() => Policy.NormalizeFolder(folder)).Rejection.ShouldBe(rejection);

    [TestCase("a.txt", "a.txt")]
    [TestCase("/a/b.txt", "a/b.txt")]                     // leading separators stripped, as archive tools do
    [TestCase(@"\\server\share\x.txt", "server/share/x.txt")] // idem (today's behaviour)
    [TestCase(@"a\b.txt", "a/b.txt")]
    [TestCase("./a//b.txt", "a/b.txt")]
    [TestCase(".ddbx/a.txt", ".ddbx/a.txt")]
    [TestCase(".hidden", ".hidden")]
    [TestCase("a/b.txt.", "a/b.txt")]                     // sanitized: trailing dot
    [TestCase("a/b:c.txt", "a/b_c.txt")]                  // sanitized: invalid character
    public void NormalizeArchiveKey_Safe_ReturnsTheSanitizedKey(string key, string expected) =>
        Policy.NormalizeArchiveKey(key).ShouldBe(NormalizedEntryPath.Accepted(expected));

    [TestCase(null)]
    [TestCase("")]
    [TestCase("  ")]
    [TestCase("/")]
    [TestCase("./")]
    [TestCase("...")]
    public void NormalizeArchiveKey_FolderOrEmpty_IsSkipped(string? key) =>
        Policy.NormalizeArchiveKey(key).Rejected.ShouldBe(EntryPathRejection.Empty);

    [TestCase(".ddb/x.txt")]
    [TestCase("sub/.ddb/x.txt")]
    [TestCase(@"a\.ddb\x.txt")]
    public void NormalizeArchiveKey_DdbSegment_IsSkipped(string key) =>
        Policy.NormalizeArchiveKey(key).Rejected.ShouldBe(EntryPathRejection.Reserved);

    [TestCase("../evil.txt", EntryPathRejection.Traversal)]
    [TestCase("a/../../evil.txt", EntryPathRejection.Traversal)]
    [TestCase(@"a\..\..\evil.txt", EntryPathRejection.Traversal)]
    [TestCase("/../evil.txt", EntryPathRejection.Traversal)]
    [TestCase(@"C:\evil.txt", EntryPathRejection.Absolute)] // was sanitized to "C_/evil.txt" on Linux
    [TestCase("C:evil.txt", EntryPathRejection.Absolute)]
    [TestCase("a\0.txt", EntryPathRejection.Nul)]           // two of the three old normalizers made it "a_.txt"
    [TestCase("sub/\0/a.txt", EntryPathRejection.Nul)]
    public void NormalizeArchiveKey_ZipSlip_Throws(string key, string rejection) =>
        Should.Throw<InvalidDatasetPathException>(() => Policy.NormalizeArchiveKey(key)).Rejection.ShouldBe(rejection);

    [TestCase("", "a.txt", "a.txt")]
    [TestCase("dest", "sub/a.txt", "dest/sub/a.txt")]
    [TestCase("dest", ".uploads/a.txt", "dest/.uploads/a.txt")]
    [TestCase("", "sub/.uploads/a.txt", "sub/.uploads/a.txt")]
    public void CheckIn_Accepted(string folder, string relative, string expected) =>
        Policy.CheckIn(folder, relative).ShouldBe(NormalizedEntryPath.Accepted(expected));

    [TestCase("", ".uploads/x.txt")] // an archive at the root may not write into the staging folder
    [TestCase("", ".uploads")]
    public void CheckIn_ReservedResult_IsReserved(string folder, string relative) =>
        Policy.CheckIn(folder, relative).Rejected.ShouldBe(EntryPathRejection.Reserved);

    [Test]
    public void ToLocalPath_StaysInsideTheFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "policy-root");
        DatasetPathPolicy.ToLocalPath(root, "a/b.txt").ShouldBe(Path.Combine(root, "a", "b.txt"));
        Should.Throw<InvalidDatasetPathException>(() => DatasetPathPolicy.ToLocalPath(root, "../policy-root2/x"))
            .Rejection.ShouldBe(EntryPathRejection.Traversal);
    }

    [Test]
    public void ResolveUntrustedPaths_SkipsReserved_AndOneUnsafePathFailsTheBatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "policy-root");
        var resolved = Policy.ResolveUntrustedPaths(root, ["a.txt", "./b.txt", ".ddb/x", ".uploads/y"], p => p);
        resolved.Select(r => r.LocalPath).ShouldBe([Path.Combine(root, "a.txt"), Path.Combine(root, "b.txt")]);

        Should.Throw<InvalidDatasetPathException>(() =>
            Policy.ResolveUntrustedPaths(root, ["a.txt", @"C:\x"], p => p)).Rejection.ShouldBe(EntryPathRejection.Absolute);
    }
}
