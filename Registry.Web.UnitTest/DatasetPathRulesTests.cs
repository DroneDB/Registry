#nullable enable
using System;
using NUnit.Framework;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <see cref="DatasetPathRules"/>: the reserved-segment and NUL-byte rules shared by every path
/// entry point (XI-09), and the archive entry normalizer the three zip-slip guards (archive extract,
/// photogrammetry inputs, archive URL import) now share.
/// </summary>
[TestFixture]
public class DatasetPathRulesTests
{
    [TestCase(".ddb/x")]
    [TestCase("sub/.ddb/x")]
    [TestCase(@"a\.ddb\x")]
    [TestCase("./.ddb/x")]
    [TestCase(".uploads/x")]
    [TestCase("./.uploads/x")]
    public void IsReserved_True(string path) => DatasetPathRules.IsReserved(path).ShouldBeTrue();

    [TestCase(".ddbx/a.txt")]
    [TestCase("sub/.uploads/x")]
    [TestCase("")]
    public void IsReserved_False(string path) => DatasetPathRules.IsReserved(path).ShouldBeFalse();

    [Test]
    public void EnsureNoNulByte_Nul_Throws() =>
        Should.Throw<ArgumentException>(() => DatasetPathRules.EnsureNoNulByte("a\0b"));

    [TestCase("a/b.txt")]
    [TestCase(null)]
    public void EnsureNoNulByte_NoNul_Passes(string? path) =>
        Should.NotThrow(() => DatasetPathRules.EnsureNoNulByte(path));

    [TestCase("a.txt", "a.txt")]
    [TestCase("/a/b.txt", "a/b.txt")]
    [TestCase(@"a\b.txt", "a/b.txt")]
    [TestCase(".ddbx/a.txt", ".ddbx/a.txt")]
    [TestCase(".hidden", ".hidden")]
    [TestCase("a/b.txt.", "a/b.txt")]
    public void NormalizeArchiveEntry_Safe_ReturnsSanitizedRelativePath(string entry, string expected) =>
        DatasetPathRules.NormalizeArchiveEntry(entry).ShouldBe(expected);

    [TestCase(null)]
    [TestCase("")]
    [TestCase("  ")]
    [TestCase("/")]
    [TestCase("...")]
    public void NormalizeArchiveEntry_Empty_ReturnsNull(string? entry) =>
        DatasetPathRules.NormalizeArchiveEntry(entry).ShouldBeNull();

    [TestCase("../evil.txt")]
    [TestCase("a/../../evil.txt")]
    [TestCase(@"a\..\..\evil.txt")]
    public void NormalizeArchiveEntry_Traversal_Throws(string entry) =>
        Should.Throw<InvalidOperationException>(() => DatasetPathRules.NormalizeArchiveEntry(entry));

    // Before XI-09 two of the three normalizers sanitized a NUL into "_" and accepted the entry.
    [TestCase("a\0.txt")]
    [TestCase("sub/\0/a.txt")]
    public void NormalizeArchiveEntry_NulByte_Throws(string entry) =>
        Should.Throw<ArgumentException>(() => DatasetPathRules.NormalizeArchiveEntry(entry));

    [TestCase("a\0.txt")]
    public void JoinArchiveEntry_NulByte_Throws(string entry) =>
        Should.Throw<ArgumentException>(() => DatasetPathRules.JoinArchiveEntry("dest", entry));

    [TestCase("", "a.txt", "a.txt")]
    [TestCase(null, "a.txt", "a.txt")]
    [TestCase("dest", "sub/a.txt", "dest/sub/a.txt")]
    [TestCase("dest", ".ddbx/a.txt", "dest/.ddbx/a.txt")]
    [TestCase("", "sub/.uploads/a.txt", "sub/.uploads/a.txt")]
    public void JoinArchiveEntry_Safe_ReturnsDatasetPath(string? dest, string entry, string expected) =>
        DatasetPathRules.JoinArchiveEntry(dest, entry).ShouldBe(expected);

    [TestCase("", ".ddb/x.txt")]
    [TestCase("", "sub/.ddb/x.txt")]
    [TestCase("", @"a\.ddb\x.txt")]
    [TestCase("dest", ".ddb/x.txt")]
    [TestCase(".ddb", "x.txt")]
    [TestCase("", ".uploads/x.txt")]
    public void JoinArchiveEntry_Reserved_Throws(string dest, string entry) =>
        Should.Throw<InvalidOperationException>(() => DatasetPathRules.JoinArchiveEntry(dest, entry));

    [TestCase("")]
    [TestCase("...")]
    public void JoinArchiveEntry_EmptyName_Throws(string entry) =>
        Should.Throw<InvalidOperationException>(() => DatasetPathRules.JoinArchiveEntry("dest", entry));

    [TestCase("../evil.txt")]
    public void JoinArchiveEntry_Traversal_Throws(string entry) =>
        Should.Throw<InvalidOperationException>(() => DatasetPathRules.JoinArchiveEntry("dest", entry));
}
