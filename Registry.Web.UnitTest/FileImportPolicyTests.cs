#nullable enable
using System;
using NUnit.Framework;
using Registry.Web.Services.Import;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Tests for <see cref="FileImportPolicy"/>: http/https URL validation and safe file-name derivation
/// used by the single-file URL import (verify path + import-file tool).
/// </summary>
[TestFixture]
public class FileImportPolicyTests
{
    [Test]
    public void ParseHttpUrl_ValidHttp_ReturnsUri()
    {
        var uri = FileImportPolicy.ParseHttpUrl("http://example.com/data/file.laz");
        uri.Host.ShouldBe("example.com");
    }

    [Test]
    public void ParseHttpUrl_ValidHttps_ReturnsUri()
    {
        var uri = FileImportPolicy.ParseHttpUrl("https://example.com/file.tif");
        uri.Scheme.ShouldBe("https");
    }

    [TestCase("ftp://example.com/file.laz")]
    [TestCase("file:///etc/passwd")]
    [TestCase("data:text/plain;base64,AAAA")]
    [TestCase("not a url")]
    [TestCase("/relative/path.laz")]
    [TestCase("")]
    [TestCase(null)]
    public void ParseHttpUrl_Invalid_Throws(string? url)
    {
        Should.Throw<ArgumentException>(() => FileImportPolicy.ParseHttpUrl(url));
    }

    [Test]
    public void DeriveFileName_FromUrlPath()
    {
        var uri = new Uri("https://example.com/folder/photo.jpg");
        FileImportPolicy.DeriveFileName(uri).ShouldBe("photo.jpg");
    }

    [Test]
    public void DeriveFileName_PrefersContentDisposition()
    {
        var uri = new Uri("https://example.com/download?id=42");
        FileImportPolicy.DeriveFileName(uri, "cloud.laz").ShouldBe("cloud.laz");
    }

    [Test]
    public void DeriveFileName_UrlEncodedSegment_IsDecoded()
    {
        var uri = new Uri("https://example.com/my%20file.tif");
        FileImportPolicy.DeriveFileName(uri).ShouldBe("my file.tif");
    }

    [Test]
    public void DeriveFileName_NoPath_FallsBack()
    {
        var uri = new Uri("https://example.com/");
        FileImportPolicy.DeriveFileName(uri).ShouldBe("imported-file");
    }

    [Test]
    public void SanitizeFileName_StripsDirectoryComponents()
    {
        FileImportPolicy.SanitizeFileName("some/nested/path/model.obj").ShouldBe("model.obj");
        FileImportPolicy.SanitizeFileName(@"C:\windows\evil.exe").ShouldBe("evil.exe");
    }

    [Test]
    public void SanitizeFileName_StripsTraversalAndQuery()
    {
        // Directory components (including "..") are dropped, leaving only the bare name.
        FileImportPolicy.SanitizeFileName("../../etc/passwd").ShouldBe("passwd");
        FileImportPolicy.SanitizeFileName("file.laz?token=abc#frag").ShouldBe("file.laz");
    }

    [Test]
    public void SanitizeFileName_ReplacesInvalidChars()
    {
        var result = FileImportPolicy.SanitizeFileName("a<b>c:d.laz");
        result.ShouldNotContain("<");
        result.ShouldNotContain(">");
        result.ShouldNotContain(":");
        result.ShouldEndWith(".laz");
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("...")]
    [TestCase("/")]
    public void SanitizeFileName_EmptyOrDots_FallsBack(string input)
    {
        FileImportPolicy.SanitizeFileName(input).ShouldBe("imported-file");
    }
}
