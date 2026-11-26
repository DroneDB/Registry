using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.OrganizationAssets;
using Registry.Web.Services.Organizations;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// What the files of an organization are made of: the image checks with the limit of the feature that asks, the type of
/// a stored file found from its bytes, where the store puts the files and what name or organization it refuses, the
/// logo that is exactly the file named <c>logo</c>, and the URL that names it.
/// </summary>
[TestFixture]
public class OrganizationAssetTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
    private static readonly byte[] WebP = [.. "RIFF"u8, 4, 0, 0, 0, .. "WEBP"u8, 1, 2];
    private static readonly byte[] Svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>");

    // What the store reads of a file to tell its type
    private static ReadOnlySpan<byte> Head(byte[] bytes) => bytes.AsSpan(0, Math.Min(bytes.Length, OrganizationImage.SniffBytes));

    private static OrganizationImage Logo(byte[] bytes) => OrganizationImage.Inspect(bytes, OrganizationLogos.MaxBytes);

    #region The image

    [Test]
    public void TheLimit_IsTheOneOfTheFeatureThatAsks()
    {
        byte[] between = [.. Png, .. new byte[OrganizationLogos.MaxBytes]];

        Should.Throw<BadRequestException>(() => Logo(between)).Message.ShouldContain("256 KB");
        Should.NotThrow(() => OrganizationImage.Inspect(between, 512 * 1024));
        Should.Throw<BadRequestException>(() => OrganizationImage.Inspect([.. between, .. new byte[512 * 1024]], 512 * 1024))
            .Message.ShouldContain("512 KB");
    }

    [Test]
    public void ALogoOfExactlyTheLimit_IsAccepted_AndOneByteOverIsNot()
    {
        Should.NotThrow(() => Logo([.. Png, .. new byte[OrganizationLogos.MaxBytes - Png.Length]]));
        Should.Throw<BadRequestException>(() => Logo([.. Png, .. new byte[OrganizationLogos.MaxBytes - Png.Length + 1]]));
    }

    [Test]
    public void TheTypeIsFoundFromTheContent()
    {
        Logo(Png).MediaType.ShouldBe("image/png");
        Logo(Jpeg).MediaType.ShouldBe("image/jpeg");
        Logo(WebP).MediaType.ShouldBe("image/webp");
        Logo(Svg).ShouldSatisfyAllConditions(l => l.MediaType.ShouldBe("image/svg+xml"), l => l.Extension.ShouldBe("svg"));
    }

    [Test]
    public void AnSvgLogo_IsStoredWithoutWhatCouldRun()
    {
        var logo = Logo(Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"><script>alert(2)</script><rect width=\"1\"/></svg>"));

        var text = Encoding.UTF8.GetString(logo.Content);
        text.ShouldContain("<rect");
        text.ShouldNotContain("alert");
        text.ShouldNotContain("script");
    }

    [TestCase("empty")]
    [TestCase("gif")]
    [TestCase("html")]
    [TestCase("riffNotWebP")]
    public void WhatIsNotAnAllowedImage_IsRefused(string kind)
    {
        var bytes = kind switch
        {
            "empty" => [],
            "gif" => Encoding.UTF8.GetBytes("GIF89a....."),
            "html" => Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"),
            _ => [.. "RIFF"u8, 4, 0, 0, 0, .. "WAVE"u8]
        };

        Should.Throw<BadRequestException>(() => Logo(bytes)).StatusCode.ShouldBe(400);
    }

    [Test]
    public void TheTypeOfAStoredFile_IsWhatItsFirstBytesSay_AndAnythingElseIsTheSvgTheUploadSanitised()
    {
        OrganizationImage.MediaTypeOfContent(Head(Png)).ShouldBe("image/png");
        OrganizationImage.MediaTypeOfContent(Head(Jpeg)).ShouldBe("image/jpeg");
        OrganizationImage.MediaTypeOfContent(Head(WebP)).ShouldBe("image/webp");
        OrganizationImage.MediaTypeOfContent(Head(Svg)).ShouldBe("image/svg+xml");
        OrganizationImage.MediaTypeOfContent([]).ShouldBe("image/svg+xml");
        OrganizationImage.MediaTypeOfContent(Png.AsSpan(0, 4)).ShouldBe("image/svg+xml", "a part of a signature is none");
    }

    [Test]
    public void TheTypeOfAStoredFile_AgreesWithTheTypeTheUploadFound()
    {
        foreach (var bytes in new[] { Png, Jpeg, WebP, Svg })
            OrganizationImage.MediaTypeOfContent(Head(bytes)).ShouldBe(Logo(bytes).MediaType);
    }

    #endregion

    #region The store

    private string _root = null!;

    [SetUp]
    public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "org-assets-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private OrganizationAssetStore Store() => new(new FileSystem(),
        Microsoft.Extensions.Options.Options.Create(new StorageOptions { StoragePath = _root }),
        NullLogger<OrganizationAssetStore>.Instance);

    private string FolderOf(string org) => Path.Combine(_root, "org-assets", org);

    [Test]
    public void TheStore_PutsAFileInTheFolderOfTheOrganization_WithNoTemporaryFileLeft()
    {
        Store().Save("acme", "logo", Png, replace: true);

        File.ReadAllBytes(Path.Combine(FolderOf("acme"), "logo")).ShouldBe(Png);
        Directory.GetFiles(FolderOf("acme")).Select(Path.GetFileName).ShouldBe(["logo"]);
        Directory.Exists(Path.Combine(_root, "branding")).ShouldBeFalse("outside the public folder");
    }

    [Test]
    public void AFileOfTheSameName_IsReplaced_WhateverItsContent()
    {
        var store = Store();
        store.Save("acme", "logo", Png, replace: true);

        store.Save("acme", "logo", Svg, replace: true);

        File.ReadAllBytes(Path.Combine(FolderOf("acme"), "logo")).ShouldBe(Svg);
        Directory.GetFiles(FolderOf("acme")).Length.ShouldBe(1);
    }

    [Test]
    public void AFileOfTheSameName_IsKept_WhenTheCallerDoesNotReplace()
    {
        var store = Store();
        store.Save("acme", "logo", Png, replace: true);

        Should.Throw<IOException>(() => store.Save("acme", "logo", Svg, replace: false));

        File.ReadAllBytes(Path.Combine(FolderOf("acme"), "logo")).ShouldBe(Png);
        Directory.GetFiles(FolderOf("acme")).Length.ShouldBe(1, "the temporary file of the refused write is gone");
    }

    [Test]
    public void AWriteThatStoppedHalfway_LeavesAFileThatIsNotTheLogo()
    {
        var store = Store();
        Directory.CreateDirectory(FolderOf("acme"));
        File.WriteAllBytes(Path.Combine(FolderOf("acme"), "logo.tmp"), Png);

        store.InfoOf("acme", "logo").ShouldBeNull();
        store.Find("acme", "logo").ShouldBeNull();
    }

    [Test]
    public void TheState_IsThatOfTheFileOnTheDisk_AndChangesWhenItIsRewritten()
    {
        var store = Store();
        store.InfoOf("acme", "logo").ShouldBeNull();
        store.Save("acme", "logo", Png, replace: true);
        var path = Path.Combine(FolderOf("acme"), "logo");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        var before = store.InfoOf("acme", "logo").ShouldNotBeNull();
        store.Save("acme", "logo", Jpeg, replace: true);
        var after = store.InfoOf("acme", "logo").ShouldNotBeNull();

        before.LastWriteTimeUtc.Ticks.ShouldBe(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).Ticks);
        after.LastWriteTimeUtc.ShouldBeGreaterThan(before.LastWriteTimeUtc);
        after.Length.ShouldBe(Jpeg.Length);
    }

    [Test]
    public void TheHead_IsTheFirstBytes_OfAFileThatMayBeShorter_AndNullWhenThereIsNone()
    {
        var store = Store();
        store.ReadHead("acme", "logo", 4).ShouldBeNull();
        store.Save("acme", "logo", Png, replace: true);
        store.Save("acme", "small", [1, 2], replace: true);

        store.ReadHead("acme", "logo", 4).ShouldBe(Png[..4]);
        store.ReadHead("acme", "small", 12).ShouldBe([1, 2]);
    }

    [Test]
    public void Removing_DeletesTheFile_AndNothingIsDoneForAFileThatIsGone()
    {
        var store = Store();
        store.Save("acme", "logo", Png, replace: true);
        store.Save("acme", "other", Png, replace: true);

        store.Remove("acme", "logo");
        store.Remove("acme", "logo");
        store.Delete("acme", "logo");

        store.InfoOf("acme", "logo").ShouldBeNull();
        store.InfoOf("acme", "other").ShouldNotBeNull();
    }

    [Test]
    public void DeletingAnOrganization_DeletesItsFolder_AndNotTheOthers()
    {
        var store = Store();
        store.Save("acme", "logo", Png, replace: true);
        store.Save("other", "logo", Png, replace: true);

        store.DeleteOrganization("acme");
        store.DeleteOrganization("acme");

        Directory.Exists(FolderOf("acme")).ShouldBeFalse();
        store.InfoOf("other", "logo").ShouldNotBeNull();
    }

    [TestCase("../x")]
    [TestCase("a/b")]
    [TestCase("a\\b")]
    [TestCase("a..b")]
    [TestCase(".hidden")]
    [TestCase("-dash")]
    [TestCase("Logo")]
    [TestCase("logo\n")]
    [TestCase("")]
    public void TheStore_RefusesAFileName_ThatCouldLeaveTheFolderOrIsNotLowerCase(string name)
    {
        var store = Store();

        Should.Throw<ArgumentException>(() => store.Save("acme", name, Png, replace: true));
        Should.Throw<ArgumentException>(() => store.InfoOf("acme", name));
        Should.Throw<ArgumentException>(() => store.Remove("acme", name));
    }

    [TestCase("../acme")]
    [TestCase("a/b")]
    [TestCase("--")]
    [TestCase("")]
    public void TheStore_RefusesAnOrganization_ThatIsNotASlug(string slug) =>
        Should.Throw<ArgumentException>(() => Store().Save(slug, "logo", Png, replace: true));

    #endregion

    #region The URL

    private OrganizationLogoLinks Links() => new(Store());

    [Test]
    public void TheUrl_IsNull_WithoutALogo_AndNamesTheVersionOfTheFile_WithOne()
    {
        var links = Links();
        links.UrlOf("acme").ShouldBeNull();

        Store().Save("acme", OrganizationAssetStore.LogoFileName, Png, replace: true);
        var path = Path.Combine(FolderOf("acme"), "logo");
        var stamp = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        links.UrlOf("acme").ShouldBe($"/orgs/acme/logo?v={stamp.Ticks}");

        File.SetLastWriteTimeUtc(path, stamp.AddSeconds(1));
        links.UrlOf("acme").ShouldBe($"/orgs/acme/logo?v={stamp.AddSeconds(1).Ticks}");
    }

    [Test]
    public void TheUrl_IsNull_ForAnOrganizationWhoseSlugIsNotValidYet_InsteadOfAnError()
    {
        // Stored before the slug rules agreed with the routes: the list that shows it must not fail for its logo
        Links().UrlOf("thomas-knig---aerial-data").ShouldBeNull();
        Links().UrlOf("a").ShouldBeNull();
    }

    [Test]
    public void TheUrl_IgnoresAFileThatIsNotTheLogo()
    {
        Store().Save("acme", "report-logo-standard.png", Png, replace: true);
        Directory.CreateDirectory(FolderOf("acme"));
        File.WriteAllBytes(Path.Combine(FolderOf("acme"), "logo.tmp"), Png);

        Links().UrlOf("acme").ShouldBeNull();
    }

    #endregion
}
