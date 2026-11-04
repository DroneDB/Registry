using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Web.Services.Storage;
using Registry.Web.Test.UpdateCheck;
using Shouldly;

namespace Registry.Web.Test.Storage;

/// <summary>
/// The JSON file in the storage folder: read leniently, written through <see cref="IFileSystem"/>, and a write the disk
/// refuses is one exception type.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~StorageDocumentTests"
/// </summary>
[TestFixture]
public class StorageDocumentTests
{
    private sealed record Note(string Text);

    private static StorageDocument<Note> Document(IFileSystem fs) =>
        new(UpdateCheckTestKit.Storage("/storage"), fs, "note.json", NullLogger.Instance);

    [Test]
    public void AMissingFile_IsNoDocument()
    {
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.ReadAllText(It.IsAny<string>())).Throws(new FileNotFoundException());

        Document(fs.Object).Read().ShouldBeNull();
    }

    [TestCase(typeof(IOException))]
    [TestCase(typeof(UnauthorizedAccessException))]
    public void AFileThatCannotBeRead_IsNoDocument(Type failure)
    {
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.ReadAllText(It.IsAny<string>())).Throws((Exception)Activator.CreateInstance(failure)!);

        Document(fs.Object).Read().ShouldBeNull();
    }

    [Test]
    public void ABugWhileReading_IsNotSwallowed()
    {
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.ReadAllText(It.IsAny<string>())).Throws<InvalidOperationException>();

        Should.Throw<InvalidOperationException>(() => Document(fs.Object).Read());
    }

    [TestCase(typeof(IOException))]
    [TestCase(typeof(UnauthorizedAccessException))]
    public void AWriteTheDiskRefuses_IsAStorageDocumentException(Type failure)
    {
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.FolderCreate(It.IsAny<string>())).Throws((Exception)Activator.CreateInstance(failure)!);

        var error = Should.Throw<StorageDocumentException>(() => Document(fs.Object).Write(new Note("x")));

        error.InnerException.ShouldBeOfType(failure);
    }

    [Test]
    public void ARoundTrip_GoesThroughTheFileSystem()
    {
        var written = "";
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.WriteAllBytesDurable(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<FileMode>(), It.IsAny<FileShare>()))
            .Callback<string, byte[], FileMode, FileShare>((_, bytes, _, _) => written = System.Text.Encoding.UTF8.GetString(bytes));
        fs.Setup(f => f.ReadAllText(It.IsAny<string>())).Returns(() => written);
        var document = Document(fs.Object);

        document.Write(new Note("hello"));

        document.Read().ShouldBe(new Note("hello"));
        fs.Verify(f => f.Move(It.IsAny<string>(), Path.GetFullPath("/storage/note.json"), true), Times.Once);
    }
}
