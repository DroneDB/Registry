#nullable enable
using System;
using System.IO;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The reserved-path rule of <see cref="UploadPathPolicy"/> matches libddb's (XI-09): a path is
/// reserved when any segment is exactly ".ddb" (ordinal, '/' or '\' separators), which libddb
/// skips when it indexes, or when its first segment is exactly ".uploads" (Registry temp files).
/// </summary>
[TestFixture]
public class UploadPathPolicyTests
{
    private string _dataset = null!;

    [SetUp]
    public void SetUp()
    {
        _dataset = Path.Combine(Path.GetTempPath(), "ddb_uploadpolicy_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataset);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dataset)) Directory.Delete(_dataset, true);
    }

    [TestCase(".ddb")]
    [TestCase(".ddb/x")]
    [TestCase("/.ddb/x")]
    [TestCase("sub/.ddb/x")]
    [TestCase("a/b/.ddb")]
    [TestCase(@"a\.ddb\x")]
    [TestCase(@".ddb\x")]
    [TestCase(".uploads")]
    [TestCase(".uploads/part.tmp")]
    [TestCase(@".uploads\part.tmp")]
    public void IsReserved_ReservedSegment_True(string path) =>
        UploadPathPolicy.IsReserved(path).ShouldBeTrue();

    [TestCase(".ddbx/a.txt")]
    [TestCase(".ddb.txt")]
    [TestCase("x.ddb")]
    [TestCase("sub/.ddbx/a.txt")]
    [TestCase(".DDB/a.txt")] // ordinal: libddb compares case-sensitively
    [TestCase(".uploadsx/a.txt")]
    [TestCase("sub/.uploads/a.txt")] // .uploads is reserved only at the dataset root
    [TestCase("a.txt")]
    public void IsReserved_NotReserved_False(string path) =>
        UploadPathPolicy.IsReserved(path).ShouldBeFalse();

    [Test]
    public void Validate_PathWithDdbLookalikeFolder_IsAccepted() =>
        Should.NotThrow(() => UploadPathPolicy.Validate(".ddbx/a.txt", _dataset, new ImportSettings()));

    [TestCase(".ddb/x.txt")]
    [TestCase("sub/.ddb/x.txt")]
    [TestCase(@"a\.ddb\x.txt")]
    public void Validate_ReservedSegment_Throws(string path) =>
        Should.Throw<InvalidOperationException>(() => UploadPathPolicy.Validate(path, _dataset, new ImportSettings()));

    [TestCase("a\0.txt")]
    [TestCase("sub/\0/a.txt")]
    public void Validate_NulByte_Throws(string path) =>
        Should.Throw<ArgumentException>(() => UploadPathPolicy.Validate(path, _dataset, new ImportSettings()));
}
