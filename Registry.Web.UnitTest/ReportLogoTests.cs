using System;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Reports;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// What a logo upload accepts (the type is the content's, never the name's), what the SVG sanitiser leaves of a
/// document, and where the store puts the files and what name it refuses.
/// </summary>
[TestFixture]
public class ReportLogoTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private static byte[] WebP() => [.. "RIFF"u8, 4, 0, 0, 0, .. "WEBP"u8, 1, 2];

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Test]
    public void TheTypeIsFoundFromTheContent()
    {
        ReportLogo.Inspect(Png).ShouldSatisfyAllConditions(l => l.Extension.ShouldBe("png"),
            l => l.MediaType.ShouldBe("image/png"));
        ReportLogo.Inspect(Jpeg).ShouldSatisfyAllConditions(l => l.Extension.ShouldBe("jpg"),
            l => l.MediaType.ShouldBe("image/jpeg"));
        ReportLogo.Inspect(WebP()).ShouldSatisfyAllConditions(l => l.Extension.ShouldBe("webp"),
            l => l.MediaType.ShouldBe("image/webp"));
        ReportLogo.Inspect(Utf8("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")).ShouldSatisfyAllConditions(
            l => l.Extension.ShouldBe("svg"), l => l.MediaType.ShouldBe("image/svg+xml"));
    }

    [Test]
    public void APngLogo_IsStoredAsItWas() => ReportLogo.Inspect(Png).Content.ShouldBe(Png);

    [TestCase("empty")]
    [TestCase("gif")]
    [TestCase("html")]
    [TestCase("text")]
    [TestCase("riffNotWebP")]
    [TestCase("svgInsideHtml")]
    [TestCase("tooLarge")]
    public void WhatIsNotAnAllowedImage_IsRefused(string kind)
    {
        var bytes = kind switch
        {
            "empty" => [],
            "gif" => Utf8("GIF89a....."),
            "html" => Utf8("<html><script>alert(1)</script></html>"),
            "text" => Utf8("just words"),
            "riffNotWebP" => [.. "RIFF"u8, 4, 0, 0, 0, .. "WAVE"u8],
            "svgInsideHtml" => Utf8("<html xmlns=\"http://www.w3.org/1999/xhtml\"><svg xmlns=\"http://www.w3.org/2000/svg\"/></html>"),
            _ => [.. Png, .. new byte[ReportLogo.MaxBytes]]
        };

        Should.Throw<BadRequestException>(() => ReportLogo.Inspect(bytes)).StatusCode.ShouldBe(400);
    }

    [Test]
    public void ALogoOfExactlyTheLimit_IsAccepted() =>
        Should.NotThrow(() => ReportLogo.Inspect([.. Png, .. new byte[ReportLogo.MaxBytes - Png.Length]]));

    private static string Sanitized(string svg) => Encoding.UTF8.GetString(SvgSanitizer.Sanitize(Utf8(svg)));

    private const string Ns = "xmlns=\"http://www.w3.org/2000/svg\"";

    [Test]
    public void AnSvg_KeepsItsShapesAndPaint()
    {
        var result = Sanitized($"<svg {Ns} viewBox=\"0 0 10 10\" width=\"10\"><g transform=\"scale(2)\">" +
                               "<path d=\"M0 0L5 5\" fill=\"#ff0000\" stroke-width=\"2\"/>" +
                               "<circle cx=\"5\" cy=\"5\" r=\"2\" style=\"fill:#00f;stroke:#000\"/>" +
                               "<text x=\"1\" y=\"2\">Acme</text></g></svg>");

        result.ShouldContain("viewBox=\"0 0 10 10\"");
        result.ShouldContain("<path d=\"M0 0L5 5\" fill=\"#ff0000\" stroke-width=\"2\"");
        result.ShouldContain("style=\"fill:#00f;stroke:#000\"");
        result.ShouldContain(">Acme</text>");
    }

    [TestCase("<script>alert(1)</script>", "script")]
    [TestCase("<script xmlns=\"http://www.w3.org/2000/svg\">alert(1)</script>", "alert")]
    [TestCase("<foreignObject><div xmlns=\"http://www.w3.org/1999/xhtml\">x</div></foreignObject>", "foreignObject")]
    [TestCase("<image href=\"http://evil.test/x.png\"/>", "evil.test")]
    [TestCase("<a href=\"javascript:alert(1)\"><rect/></a>", "javascript")]
    [TestCase("<style>rect{fill:url(http://evil.test/x)}</style>", "evil.test")]
    [TestCase("<rect onload=\"alert(1)\" onclick=\"alert(2)\" width=\"1\"/>", "alert")]
    [TestCase("<rect fill=\"url(http://evil.test/x#a)\" width=\"1\"/>", "evil.test")]
    [TestCase("<rect style=\"fill:URL( 'https://evil.test/x' )\" width=\"1\"/>", "evil.test")]
    [TestCase("<rect style=\"fill:u\\72l(http://evil.test/x)\" width=\"1\"/>", "evil.test")]
    [TestCase("<use href=\"http://evil.test/x.svg#a\"/>", "evil.test")]
    [TestCase("<use xmlns:xlink=\"http://www.w3.org/1999/xlink\" xlink:href=\"data:image/svg+xml;base64,AAAA\"/>", "data:")]
    [TestCase("<rect style=\"behavior:url(x.htc);background:url(http://evil.test)\" width=\"1\"/>", "evil.test")]
    [TestCase("<set attributeName=\"href\" to=\"javascript:alert(1)\"/>", "javascript")]
    [TestCase("<animate attributeName=\"href\" values=\"javascript:alert(1)\"/>", "javascript")]
    public void AnSvg_LosesWhatCouldRunOrReachOut(string inner, string forbidden)
    {
        var result = Sanitized($"<svg {Ns}>{inner}</svg>");

        result.ShouldNotContain(forbidden, Case.Insensitive);
        result.ShouldStartWith("<svg");
    }

    [Test]
    public void AnSvg_KeepsAReferenceToAnIdOfItsOwn()
    {
        var result = Sanitized($"<svg {Ns}><defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#fff\"/></linearGradient></defs>" +
                               "<rect fill=\"url(#g)\" width=\"1\"/><use href=\"#g\"/></svg>");

        result.ShouldContain("fill=\"url(#g)\"");
        result.ShouldContain("<use href=\"#g\"");
        result.ShouldContain("<stop");
    }

    [Test]
    public void AnSvg_WithADoctypeAndAnEntity_IsNotExpandedOrFetched()
    {
        const string billionLaughs = "<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;\">]>" +
                                     $"<svg {Ns}><text>&b;</text></svg>";

        // An entity of the document is not defined for the reader (ignored DTD): the document is refused, never expanded
        Should.Throw<InvalidDataException>(() => SvgSanitizer.Sanitize(Utf8(billionLaughs)));

        const string external = "<!DOCTYPE svg SYSTEM \"http://evil.test/x.dtd\">";
        Sanitized($"{external}<svg {Ns}><rect width=\"1\"/></svg>").ShouldNotContain("evil.test");
    }

    [Test]
    public void AnSvg_NestedTooDeeply_IsRefused()
    {
        var deep = new StringBuilder($"<svg {Ns}>");
        for (var i = 0; i < SvgSanitizer.MaxDepth + 5; i++)
            deep.Append("<g>");

        Should.Throw<InvalidDataException>(() => SvgSanitizer.Sanitize(Utf8(deep.ToString())));
    }

    [Test]
    public void ADocumentThatIsNotAnSvg_IsRefused()
    {
        Should.Throw<InvalidDataException>(() => SvgSanitizer.Sanitize(Utf8("<svg><rect></svg>")));
        Should.Throw<InvalidDataException>(() => SvgSanitizer.Sanitize(Utf8("<svg><rect/></svg>")), "no SVG namespace");
        Should.Throw<InvalidDataException>(() => SvgSanitizer.Sanitize(Utf8($"<g {Ns}/>")), "the root is not svg");
    }

    #region Store

    private string _root = null!;

    [SetUp]
    public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "report-logo-store-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private ReportLogoStore Store() => new(new FileSystem(),
        Microsoft.Extensions.Options.Options.Create(new StorageOptions { StoragePath = _root }), NullLogger<ReportLogoStore>.Instance);

    [Test]
    public void TheStore_PutsTheLogoOutsideTheBrandingFolder_InAFileOfEachUpload()
    {
        var store = Store();

        var name = store.Save("acme", "standard", ReportLogo.Inspect(Png));
        var again = store.Save("acme", "standard", ReportLogo.Inspect([.. Png, 9]));

        name.ShouldBeLogoOf("standard", "png");
        again.ShouldBeLogoOf("standard", "png");
        again.ShouldNotBe(name, "two uploads never write one file");
        var path = store.Find("acme", name).ShouldNotBeNull();
        path.ShouldBe(Path.Combine(_root, "org-assets", "acme", name));
        File.ReadAllBytes(path).ShouldBe(Png);
        File.ReadAllBytes(store.Find("acme", again).ShouldNotBeNull()).ShouldBe([.. Png, 9]);
        Directory.GetFiles(Path.GetDirectoryName(path)!).Length.ShouldBe(2, "no temporary file is left");
        Directory.Exists(Path.Combine(_root, "branding")).ShouldBeFalse();
    }

    [Test]
    public void TheStore_DeletesAFile_AndAnOrganizationsFolder()
    {
        var store = Store();
        var a = store.Save("acme", "a", ReportLogo.Inspect(Png));
        var b = store.Save("acme", "b", ReportLogo.Inspect(Jpeg));
        var otherA = store.Save("other", "a", ReportLogo.Inspect(Png));

        store.Delete("acme", a);
        store.Delete("acme", a);
        store.Find("acme", a).ShouldBeNull();
        store.Find("acme", b).ShouldNotBeNull();

        store.DeleteOrganization("acme");
        store.DeleteOrganization("acme");
        Directory.Exists(Path.Combine(_root, "org-assets", "acme")).ShouldBeFalse();
        store.Find("other", otherA).ShouldNotBeNull();
    }

    [TestCase("report-logo-standard.png")]
    [TestCase("report-logo-standard-0123456789abcdef0123456789abcdef.svg")]
    [TestCase("report-logo-a-b-0123456789abcdef0123456789abcdef.jpg")]
    public void TheStore_KnowsTheNamesOfItsFiles_AndThoseStoredBeforeEachUploadHadItsOwn(string name)
    {
        var store = Store();
        Directory.CreateDirectory(Path.Combine(_root, "org-assets", "acme"));
        File.WriteAllBytes(Path.Combine(_root, "org-assets", "acme", name), Png);

        ReportLogoStore.IsLogoFileName(name).ShouldBeTrue();
        store.Find("acme", name).ShouldNotBeNull();
        store.Delete("acme", name);
        store.Find("acme", name).ShouldBeNull();
    }

    [TestCase("../x.png")]
    [TestCase("report-logo-..%2Fx.png")]
    [TestCase("report-logo-a.exe")]
    [TestCase("report-logo-A.png")]
    [TestCase("report-logo-a.png\n")]
    [TestCase("report-logo-a-0123456789ABCDEF0123456789ABCDEF.png")]
    [TestCase("report-logo-a-0123456789abcdef0123456789abcdef.png.tmp")]
    [TestCase("logo.png")]
    [TestCase("/etc/passwd")]
    public void TheStore_RefusesAName_ThatIsNotALogoFileName(string name)
    {
        var store = Store();

        ReportLogoStore.IsLogoFileName(name).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => store.Find("acme", name));
    }

    [TestCase("../acme")]
    [TestCase("a/b")]
    [TestCase("")]
    public void TheStore_RefusesAnOrganization_ThatIsNotASlug(string slug) =>
        Should.Throw<ArgumentException>(() => Store().Save(slug, "standard", ReportLogo.Inspect(Png)));

    #endregion
}

/// <summary>The names the logo store gives its files, as tests check them.</summary>
internal static class ReportLogoNames
{
    /// <summary>A logo file of <paramref name="templateId"/> and of the type <paramref name="extension"/>, of one upload.</summary>
    public static void ShouldBeLogoOf(this string? name, string templateId, string extension) =>
        name.ShouldNotBeNull().ShouldMatch($@"^report-logo-{templateId}-[0-9a-f]{{32}}\.{extension}$");
}
