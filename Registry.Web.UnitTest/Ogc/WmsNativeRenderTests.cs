using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Adapters.Testing;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;
using SkiaSharp;

namespace Registry.Web.Test.Ogc;

/// <summary>
/// A GetMap of one layer is the image libddb renders in the requested format, over BGCOLOR unless TRANSPARENT=TRUE:
/// Registry serves its bytes as they come. A GetMap of several layers composites their RGBA pixels with SkiaSharp,
/// with no PNG encoded and decoded per layer, and the same blending as when it decoded them.
/// </summary>
[TestFixture]
public class WmsNativeRenderTests
{
    private const string Org = "org";
    private const string Ds = "ds";
    private const int Width = 64;
    private const int Height = 48;
    private static readonly double[] View = [1000.0, 2000.0, 1128.0, 2096.0];

    // Not a decodable image: an encode of it in managed code would fail the test
    private static readonly byte[] NativeImage = [0x89, 0x50, 0x4E, 0x47, 7, 7, 7];

    private static readonly string[] LayerNames = ["dem", "ortho", "ms"];

    private Mock<FakeDdbWrapper> _wrapper = null!;
    private WmsManager _wms = null!;

    private static string Cog(string layer) => $"/data/{layer}-cog.tif";

    [SetUp]
    public void SetUp()
    {
        var dataset = new Dataset { Slug = Ds, InternalRef = Guid.NewGuid() };
        var utils = new Mock<IDatasetResolver>();
        utils.Setup(u => u.GetDatasetAsync(Org, Ds, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dataset);
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.RequestAccess(dataset, AccessType.Read)).ReturnsAsync(true);
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get(Org, dataset.InternalRef)).Returns(Mock.Of<IDDB>());

        var artifacts = new Mock<IBuildArtifactResolver>();
        artifacts.Setup(a => a.FindArtifactFile(It.IsAny<IDDB>(), It.IsAny<string>(), ArtifactRoles.RasterCog, null,
                It.IsAny<string?>()))
            .Returns((IDDB _, string _, string _, string? _, string? entryPath) =>
                Cog(System.IO.Path.GetFileNameWithoutExtension(entryPath!)));

        var layers = LayerNames.Select(name => new OgcLayerDto
        {
            Name = name, Title = name, EntryType = EntryType.GeoRaster, EntryPath = name + ".tif", EntryHash = "h-" + name
        }).ToList();
        var catalog = new Mock<IOgcLayerCatalog>();
        catalog.Setup(c => c.ResolveAsync(Org, Ds, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string name, string? _, bool _, CancellationToken _) =>
                layers.FirstOrDefault(l => l.Name == name));

        _wrapper = new Mock<FakeDdbWrapper> { CallBase = true };
        _wrapper.Setup(w => w.GetRasterInfo(Cog("ms"))).Returns(FakeDdbWrapper.RasterInfo("NDVI"));
        _wrapper.Setup(w => w.RenderRasterRegion(It.IsAny<string>(), It.IsAny<double[]>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int[]?>(), It.IsAny<string?>(),
                It.IsAny<uint?>()))
            .Returns(NativeImage);
        _wrapper.Setup(w => w.RenderRasterIndex(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double[]>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<uint?>()))
            .Returns(NativeImage);
        foreach (var name in LayerNames)
        {
            var pixels = LayerPixels(name);
            _wrapper.Setup(w => w.RenderRasterRegionPixels(Cog(name), It.IsAny<double[]>(), "EPSG:3857", Width,
                    Height, It.IsAny<RasterPixelsReader>()))
                .Callback((string _, double[] _, string _, int _, int _, RasterPixelsReader read) => read(pixels));
            _wrapper.Setup(w => w.RenderRasterIndexPixels(Cog(name), It.IsAny<string>(), It.IsAny<double[]>(),
                    "EPSG:3857", Width, Height, It.IsAny<RasterPixelsReader>()))
                .Callback((string _, string _, double[] _, string _, int _, int _, RasterPixelsReader read) =>
                    read(LayerPixels(name + "-index")));
        }

        IDistributedCache store = TestCaches.MemoryStore();
        var cacheManager = new CacheManager(store, NullLogger<CacheManager>.Instance,
            new NullCacheKeyScanner(NullLogger<NullCacheKeyScanner>.Instance));
        cacheManager.Register(MagicStrings.OgcRenderCacheSeed, CacheProviderFactories.CreatePassThroughProvider(),
            TimeSpan.FromMinutes(10));
        var records = TestCaches.Records(store);
        _wms = new WmsManager(
            new OgcManagerDependencies(
                new DatasetAccessResolver(utils.Object, auth.Object, ddbManager.Object, TestPaths.Policy),
                artifacts.Object, _wrapper.Object, catalog.Object, records,
                new HttpContextAccessor { HttpContext = new DefaultHttpContext() }),
            NullLogger<WmsManager>.Instance, cacheManager,
            new RasterIndexCatalog(new RasterInfoCache(_wrapper.Object, records), NullLogger<RasterIndexCatalog>.Instance));
    }

    // Straight RGBA of a layer: every colour and alpha from transparent to opaque, a pattern of its own per layer
    private static byte[] LayerPixels(string layer)
    {
        var seed = layer.Aggregate(17, (h, c) => h * 31 + c);
        var pixels = new byte[Width * Height * 4];
        for (var i = 0; i < Width * Height; i++)
        {
            var x = i % Width;
            var y = i / Width;
            pixels[i * 4] = (byte)(x * 4 + seed);
            pixels[i * 4 + 1] = (byte)(y * 5 + seed / 3);
            pixels[i * 4 + 2] = (byte)(x * y + seed / 7);
            pixels[i * 4 + 3] = (byte)((x * 7 + y * 3 + seed) % 256);
        }

        return pixels;
    }

    private Task<byte[]> GetMap(string[] layers, string format, bool transparent, string? bgColor = null,
        string[]? styles = null) =>
        _wms.GetMapAsync(Org, Ds, layers, styles ?? layers.Select(_ => "").ToArray(), View, "EPSG:3857", Width,
            Height, format, bgColor, transparent);

    private int Calls(string method) => _wrapper.Invocations.Count(i => i.Method.Name == method);

    // TRANSPARENT absent is FALSE: the background is BGCOLOR, white when absent or invalid
    [TestCase("image/png", null, 0xFFFFFFu)]
    [TestCase("image/png", "0x336699", 0x336699u)]
    [TestCase("image/png", "nonsense", 0xFFFFFFu)]
    [TestCase("image/webp", "0xFF0000", 0xFF0000u)]
    [TestCase("image/jpeg", null, 0xFFFFFFu)]
    [TestCase("image/jpeg", "0x10E020", 0x10E020u)]
    public async Task OneOpaqueLayer_IsTheNativeRenderOverItsBackground(string format, string? bgColor,
        uint background)
    {
        var image = await GetMap(["ortho"], format, false, bgColor);

        image.ShouldBe(NativeImage);
        _wrapper.Verify(w => w.RenderRasterRegion(Cog("ortho"), It.IsAny<double[]>(), "EPSG:3857", Width, Height,
            format, null, null, background), Times.Once);
        Calls(nameof(IDdbWrapper.RenderRasterRegionPixels)).ShouldBe(0);
    }

    // PNG and WebP keep the transparency; JPEG has none, and was black there (its colours premultiplied)
    [TestCase("image/png", null)]
    [TestCase("image/webp", null)]
    [TestCase("image/jpeg", 0x000000u)]
    public async Task OneTransparentLayer_IsTheNativeRender(string format, uint? background)
    {
        var image = await GetMap(["ortho"], format, true, "0x336699");

        image.ShouldBe(NativeImage);
        _wrapper.Verify(w => w.RenderRasterRegion(Cog("ortho"), It.IsAny<double[]>(), "EPSG:3857", Width, Height,
            format, null, null, background), Times.Once);
    }

    [Test]
    public async Task OneIndexLayer_IsTheNativeIndexRenderOverItsBackground()
    {
        var image = await GetMap(["ms"], "image/webp", false, "0x336699", ["ndvi"]);

        image.ShouldBe(NativeImage);
        _wrapper.Verify(w => w.RenderRasterIndex(Cog("ms"), "NDVI", It.IsAny<double[]>(), "EPSG:3857", Width, Height,
            "image/webp", 0x336699u), Times.Once);
        Calls(nameof(IDdbWrapper.RenderRasterRegion)).ShouldBe(0);
    }

    // The composite of several layers from their pixels is the one of their PNGs decoded, pixel for pixel: Skia
    // premultiplies straight RGBA as its PNG decoder did
    [TestCase("image/png", true, null)]
    [TestCase("image/png", false, null)]
    [TestCase("image/png", false, "0x336699")]
    [TestCase("image/webp", true, null)]
    [TestCase("image/jpeg", false, "0x10E020")]
    [TestCase("image/jpeg", true, null)]
    public async Task ManyLayers_AreCompositedFromTheirPixelsAsFromTheirPngs(string format, bool transparent,
        string? bgColor)
    {
        var image = await GetMap(["dem", "ortho", "ms"], format, transparent, bgColor, ["", "default", "NDVI"]);

        Calls(nameof(IDdbWrapper.RenderRasterRegion)).ShouldBe(0);
        Calls(nameof(IDdbWrapper.RenderRasterIndex)).ShouldBe(0);
        Calls(nameof(IDdbWrapper.RenderRasterRegionPixels)).ShouldBe(2);
        Calls(nameof(IDdbWrapper.RenderRasterIndexPixels)).ShouldBe(1);
        var expected = CompositeOfPngs([LayerPixels("dem"), LayerPixels("ortho"), LayerPixels("ms-index")],
            format, transparent, bgColor ?? "FFFFFF");
        Decode(image).ShouldBe(Decode(expected));
    }

    // A layer that fails is left out of the composite (transparent), and the image is not cached
    [Test]
    public async Task ManyLayers_OneFails_IsTheOthersAndIsNotCached()
    {
        _wrapper.Setup(w => w.RenderRasterRegionPixels(Cog("ortho"), It.IsAny<double[]>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<RasterPixelsReader>()))
            .Throws(new InvalidOperationException("warp failed"));

        var image = await GetMap(["dem", "ortho"], "image/png", true);
        await GetMap(["dem", "ortho"], "image/png", true);

        Decode(image).ShouldBe(Decode(CompositeOfPngs([LayerPixels("dem")], "image/png", true, "FFFFFF")));
        Calls(nameof(IDdbWrapper.RenderRasterRegionPixels)).ShouldBe(4, "rendered again: a degraded image is not cached");
    }

    // A single layer whose native render fails is composited from its pixels, as before
    [Test]
    public async Task OneLayer_WhoseRenderFails_IsCompositedFromItsPixels()
    {
        _wrapper.Setup(w => w.RenderRasterRegion(It.IsAny<string>(), It.IsAny<double[]>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int[]?>(), It.IsAny<string?>(),
                It.IsAny<uint?>()))
            .Throws(new InvalidOperationException("encode failed"));

        var image = await GetMap(["ortho"], "image/png", false);

        Decode(image).ShouldBe(Decode(CompositeOfPngs([LayerPixels("ortho")], "image/png", false, "FFFFFF")));
    }

    // What GetMap made of several layers when libddb rendered each as a PNG: decoded, drawn over the background and
    // encoded with quality 90
    private static byte[] CompositeOfPngs(IEnumerable<byte[]> layers, string format, bool transparent, string bgColor)
    {
        using var canvas = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var surface = SKSurface.Create(canvas.Info, canvas.GetPixels(), canvas.RowBytes))
        {
            var rgb = Convert.ToUInt32(bgColor.Replace("0x", ""), 16);
            surface.Canvas.Clear(transparent
                ? SKColors.Transparent
                : new SKColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            foreach (var pixels in layers)
            {
                using var bmp = SKBitmap.Decode(Png(pixels));
                using var paint = new SKPaint { BlendMode = SKBlendMode.SrcOver };
                surface.Canvas.DrawBitmap(bmp, new SKRect(0, 0, Width, Height), SKSamplingOptions.Default, paint);
            }

            surface.Canvas.Flush();
        }

        using var image = SKImage.FromBitmap(canvas);
        var skFormat = format switch
        {
            "image/jpeg" => SKEncodedImageFormat.Jpeg,
            "image/webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png
        };
        using var data = image.Encode(skFormat, 90);
        return data.ToArray();
    }

    // The PNG libddb wrote of straight RGBA pixels
    private static byte[] Png(byte[] pixels)
    {
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var image = SKImage.FromPixels(
                new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul),
                handle.AddrOfPinnedObject(), Width * 4);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    private static byte[] Decode(byte[] image)
    {
        using var bitmap = SKBitmap.Decode(image);
        bitmap.ShouldNotBeNull();
        using var rgba = bitmap.Copy(SKColorType.Rgba8888);
        return rgba.Bytes;
    }
}
