using System.Linq;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Services.Objects;
using Shouldly;
using SkiaSharp;

namespace Registry.Web.Test.E2eFixes;

/// <summary><see cref="TileFormat"/>: the one table of tile formats (extension, media type, libddb name, encoder).</summary>
[TestFixture]
public class TileFormatTests
{
    [TestCase("png", "image/png", "png")]
    [TestCase("PNG", "image/png", "png")]
    [TestCase(".webp", "image/webp", "webp")]
    [TestCase("jpg", "image/jpeg", "jpeg")]
    public void FromExtension_NamesTheMediaTypeAndLibddbFormat(string extension, string mediaType, string ddbName)
    {
        var format = TileFormat.FromExtension(extension);

        format.MediaType.ShouldBe(mediaType);
        format.DdbName.ShouldBe(ddbName);
    }

    [TestCase("gif")]
    [TestCase("jpeg")]
    [TestCase("")]
    [TestCase(null)]
    public void FromExtension_Unknown_IsABadRequest(string? extension) =>
        Should.Throw<BadRequestException>(() => TileFormat.FromExtension(extension));

    [Test]
    public void RouteExtensions_AreTheFormatsOfTheTable() =>
        TileFormat.RouteExtensions.Split('|').ShouldBe(["png", "webp", "jpg"]);

    [Test]
    public void FromPng_EncodesInTheFormat_AndKeepsAPngAsIs()
    {
        using var bitmap = new SKBitmap(4, 4);
        bitmap.Erase(SKColors.Red);
        var png = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100).ToArray();

        TileFormat.Png.FromPng(png).ShouldBeSameAs(png);
        TileFormat.Jpeg.FromPng(png).Take(2).ShouldBe([(byte)0xFF, (byte)0xD8]);
        System.Text.Encoding.ASCII.GetString(TileFormat.Webp.FromPng(png), 8, 4).ShouldBe("WEBP");
    }
}
