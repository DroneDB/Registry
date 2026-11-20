using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Services.Objects;
using Shouldly;

namespace Registry.Web.Test.E2eFixes;

/// <summary><see cref="TileFormat"/>: the one table of tile formats (extension, media type, libddb name).</summary>
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
}
