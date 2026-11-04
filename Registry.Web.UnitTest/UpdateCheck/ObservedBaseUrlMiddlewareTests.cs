using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Web.Middlewares;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using Registry.Web.Services.UpdateCheck;
using Shouldly;

namespace Registry.Web.Test.UpdateCheck;

/// <summary>
/// The middleware that remembers the public URL: a request never fails because the disk would not take the note, and a
/// bug is not hidden.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~ObservedBaseUrlMiddlewareTests"
/// </summary>
[TestFixture]
public class ObservedBaseUrlMiddlewareTests
{
    private Mock<IFileSystem> _fs = null!;
    private Mock<IUrlBuilder> _urls = null!;

    [SetUp]
    public void SetUp()
    {
        _fs = new Mock<IFileSystem>();
        _fs.Setup(f => f.ReadAllText(It.IsAny<string>())).Throws<FileNotFoundException>();
        _urls = new Mock<IUrlBuilder>();
        _urls.Setup(u => u.GetLocalHost(It.IsAny<bool>())).Returns("https://hub.example.org");
    }

    private async Task<bool> Invoke(string? externalUrl = null)
    {
        var store = new ObservedBaseUrlStore(UpdateCheckTestKit.Storage("/storage"), _fs.Object, TimeProvider.System,
            NullLogger<ObservedBaseUrlStore>.Instance);
        var middleware = new ObservedBaseUrlMiddleware(_urls.Object, store,
            Microsoft.Extensions.Options.Options.Create(new HttpHostOptions { ExternalUrlOverride = externalUrl }), NullLogger<ObservedBaseUrlMiddleware>.Instance);

        var reachedNext = false;
        await middleware.InvokeAsync(new DefaultHttpContext(), _ =>
        {
            reachedNext = true;
            return Task.CompletedTask;
        });
        return reachedNext;
    }

    [TestCase(typeof(IOException))]
    [TestCase(typeof(UnauthorizedAccessException))]
    public async Task ADiskThatRefusesTheNote_DoesNotFailTheRequest(Type failure)
    {
        _fs.Setup(f => f.FolderCreate(It.IsAny<string>())).Throws((Exception)Activator.CreateInstance(failure)!);

        (await Invoke()).ShouldBeTrue();
        _fs.Verify(f => f.FolderCreate(It.IsAny<string>()), Times.Once);
    }

    [Test]
    public async Task AConfiguredExternalUrl_LeavesTheDiskAlone()
    {
        (await Invoke("https://configured.example.org")).ShouldBeTrue();

        _fs.Verify(f => f.FolderCreate(It.IsAny<string>()), Times.Never);
        _fs.Verify(f => f.ReadAllText(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task ABugInTheUrlBuilder_IsNotHidden()
    {
        _urls.Setup(u => u.GetLocalHost(It.IsAny<bool>())).Throws<InvalidOperationException>();

        await Should.ThrowAsync<InvalidOperationException>(() => Invoke());
    }
}
