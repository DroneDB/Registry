using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Moq;
using NUnit.Framework;
using Registry.Adapters.ObjectSystem;
using Registry.Adapters.ObjectSystem.Model;
using Registry.Common;
using Registry.Ports.ObjectSystem;
using Registry.Ports.ObjectSystem.Model;

namespace Registry.Adapters.Test.ObjectSystem
{
    [TestFixture]
    public class CachedS3ObjectSystemTest
    {
        private Logger<CachedS3ObjectSystem> _objectSystemLogger;

        [SetUp]
        public void Setup()
        {
            _objectSystemLogger = new Logger<CachedS3ObjectSystem>(LoggerFactory.Create(builder =>
                builder.AddSimpleConsole(options =>
                {
                    options.IncludeScopes = true;
                    options.SingleLine = true;
                    options.TimestampFormat = "HH:mm:ss ";
                })));
        }

        [Test]
        public async Task GetObject_Stream_ConcurrentMisses()
        {
            using var fs = new TestArea(nameof(GetObject_Stream_ConcurrentMisses));

            var settings = new CachedS3ObjectSystemSettings
            {
                Endpoint = "localhost:9000",
                AccessKey = "minioadmin",
                SecretKey = "minioadmin",
                UseSsl = false,
                AppName = "Registry",
                AppVersion = "1.0",
                Region = "us-east-1",
                CachePath = fs.TestFolder,
                BridgeUrl = "http://localhost:5000/_bridge"
            };

            const string bucket = "admin";
            const string name = "test.txt";

            var remoteStorage = new Mock<IObjectSystem>();

            var content = Encoding.UTF8.GetBytes("Test Test Test");

            remoteStorage
                .Setup(system => system.GetObjectAsync(bucket, name, It.IsAny<Action<Stream>>(),
                    It.IsAny<IServerEncryption>(), It.IsAny<CancellationToken>()))
                .Callback((string bucketName, string objectName, Action<Stream> action, IServerEncryption sse,
                    CancellationToken token) =>
                {
                    Thread.Sleep(2000);
                    action(new MemoryStream(content));
                })
                .Returns(Task.CompletedTask);

            var objectSystem = new CachedS3ObjectSystem(settings, () => remoteStorage.Object, _objectSystemLogger);

            Parallel.For(0, 100,
                i =>
                {
                    objectSystem.GetObjectAsync(bucket, name, stream => stream.CopyTo(new MemoryStream())).Wait();
                });

            remoteStorage.Verify(system => system.GetObjectAsync(bucket, name, It.IsAny<Action<Stream>>(),
                It.IsAny<IServerEncryption>(), It.IsAny<CancellationToken>()), Times.Once());
        }

        [Test]
        public async Task GetObject_File_ConcurrentMisses()
        {
            using var fs = new TestArea(nameof(GetObject_File_ConcurrentMisses));

            var settings = new CachedS3ObjectSystemSettings
            {
                Endpoint = "localhost:9000",
                AccessKey = "minioadmin",
                SecretKey = "minioadmin",
                UseSsl = false,
                AppName = "Registry",
                AppVersion = "1.0",
                Region = "us-east-1",
                CachePath = fs.TestFolder,
                BridgeUrl = "http://localhost:5000/_bridge"
            };

            const string bucket = "admin";
            const string name = "test.txt";
            const int count = 100;

            var remoteStorage = new Mock<IObjectSystem>();

            var content = Encoding.UTF8.GetBytes("Test Test Test");
            var path = Path.Combine(fs.TestFolder, name);

            remoteStorage
                .Setup(system => system.GetObjectAsync(bucket, name, It.IsAny<string>(),
                    It.IsAny<IServerEncryption>(), It.IsAny<CancellationToken>()))
                .Callback((string bucketName, string objectName, string filePath, IServerEncryption sse,
                    CancellationToken token) =>
                {
                    File.WriteAllBytes(filePath, content);
                })
                .Returns(Task.CompletedTask);

            var objectSystem = new CachedS3ObjectSystem(settings, () => remoteStorage.Object, _objectSystemLogger);

            Parallel.For(0, count,
                i => { objectSystem.GetObjectAsync(bucket, name, Path.Combine(fs.TestFolder, $"{i}.txt")).Wait(); });

            remoteStorage.Verify(system => system.GetObjectAsync(bucket, name, It.IsAny<string>(),
                It.IsAny<IServerEncryption>(), It.IsAny<CancellationToken>()), Times.Once());

            for (var n = 0; n < count; n++)
            {
                (await File.ReadAllBytesAsync(Path.Combine(fs.TestFolder, $"{n}.txt"))).Should()
                    .BeEquivalentTo(content);
            }
        }

        [Test]
        public async Task PutObject_ConcurrentWithSync_Ok()
        {
            using var fs = new TestArea(nameof(PutObject_ConcurrentWithSync_Ok));

            var settings = new CachedS3ObjectSystemSettings
            {
                Endpoint = "localhost:9000",
                AccessKey = "minioadmin",
                SecretKey = "minioadmin",
                UseSsl = false,
                AppName = "Registry",
                AppVersion = "1.0",
                Region = "us-east-1",
                CachePath = fs.TestFolder,
                BridgeUrl = "http://localhost:5000/_bridge"
            };

            const string bucket = "admin";
            const string name = "test.txt";
            const int count = 10;

            var remoteStorage = new Mock<IObjectSystem>();

            var contentStr = "Test Test Test";
            var content = Encoding.UTF8.GetBytes(contentStr);

            remoteStorage.Setup(system => system.PutObjectAsync(bucket, name, It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<Dictionary<string, string>>(),
                    It.IsAny<IServerEncryption>(), It.IsAny<CancellationToken>()))
                .Callback((string bucketName, string objectName, string filePath, string contentType,
                    Dictionary<string, string> metaData, IServerEncryption sse, CancellationToken cancellationToken) =>
                {
                    File.WriteAllText(filePath, contentStr);
                    Thread.Sleep(2000);
                }).Returns(Task.CompletedTask);

            var objectSystem = new CachedS3ObjectSystem(settings, () => remoteStorage.Object, _objectSystemLogger);

            Parallel.For(0, count,
                i =>
                {
                    var memory = new MemoryStream(content);
                    objectSystem.PutObjectAsync(bucket, name, memory, memory.Length, "text/plain", null, null, default)
                        .Wait();
                });

            (await File.ReadAllTextAsync(Path.Combine(fs.TestFolder, bucket, "descriptors", name + ".json"))).Should()
                .Be(
                    "{\"SyncTime\":null,\"LastError\":null,\"Info\":{\"ContentType\":\"text/plain\",\"MetaData\":null,\"SSE\":null}}");

            (await File.ReadAllTextAsync(Path.Combine(fs.TestFolder, bucket, "files", name))).Should().Be(contentStr);

            remoteStorage.Verify(system => system.PutObjectAsync(bucket, name, It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(),
                It.IsAny<IServerEncryption>(), It.IsAny<CancellationToken>()), Times.Never());

            await objectSystem.Sync();

            remoteStorage.Verify(system => system.PutObjectAsync(bucket, name, It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(),
                It.IsAny<IServerEncryption>(), It.IsAny<CancellationToken>()), Times.Once());

            (await objectSystem.GetObjectInfoAsync(bucket, name)).Should().NotBeNull();

        }

        [Test]
        public async Task PutObject_SyncTbd_Ok()
        {
            using var fs = new TestArea(nameof(PutObject_SyncTbd_Ok));

            var settings = new CachedS3ObjectSystemSettings
            {
                Endpoint = "localhost:9000",
                AccessKey = "minioadmin",
                SecretKey = "minioadmin",
                UseSsl = false,
                AppName = "Registry",
                AppVersion = "1.0",
                Region = "us-east-1",
                CachePath = fs.TestFolder,
                BridgeUrl = "http://localhost:5000/_bridge"
            };

            const string bucket = "admin";
            const string name = "test.txt";
            //const int count = 10;

            var remoteStorage = new Mock<IObjectSystem>();

            var contentStr = "Test Test Test";
            var content = Encoding.UTF8.GetBytes(contentStr);

            remoteStorage.Setup(system => system.PutObjectAsync(bucket, name, It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<Dictionary<string, string>>(),
                    It.IsAny<IServerEncryption>(), It.IsAny<CancellationToken>()))
                .Callback((string bucketName, string objectName, string filePath, string contentType,
                    Dictionary<string, string> metaData, IServerEncryption sse, CancellationToken cancellationToken) =>
                {
                    File.WriteAllText(filePath, contentStr);
                    Thread.Sleep(2000);
                }).Returns(Task.CompletedTask);

            var objectSystem = new CachedS3ObjectSystem(settings, () => remoteStorage.Object, _objectSystemLogger);

            var memory = new MemoryStream(content);
            objectSystem.PutObjectAsync(bucket, name, memory, memory.Length, "text/plain", null, null, default)
                .Wait();

            File.Exists(Path.Combine(fs.TestFolder, bucket, "files", name)).Should().BeTrue();

            await objectSystem.Sync();
            File.Exists(Path.Combine(fs.TestFolder, bucket, "files", name)).Should().BeTrue();

            await objectSystem.RemoveObjectAsync(bucket, name);

            File.Exists(Path.Combine(fs.TestFolder, bucket, "files", name)).Should().BeFalse();
        }
        /*
        [Test]
        public async Task DeleteObject_Multiple_Ok()
        {
            using var fs = new TestArea(nameof(DeleteObject_Multiple_Ok));

            var settings = new CachedS3ObjectSystemSettings
            {
                Endpoint = "localhost:9000",
                AccessKey = "minioadmin",
                SecretKey = "minioadmin",
                UseSsl = false,
                AppName = "Registry",
                AppVersion = "1.0",
                Region = "us-east-1",
                CachePath = fs.TestFolder,
                BridgeUrl = "http://localhost:5000/_bridge"
            };

            const string bucket = "admin";
            const string name = "test.txt";
            const int count = 10;

            var remoteStorage = new Mock<IObjectSystem>();

            var contentStr = "Test Test Test";
            var content = Encoding.UTF8.GetBytes(contentStr);

            remoteStorage.Setup(system => system.PutObjectAsync(bucket, name, It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<Dictionary<string, string>>(),
                    It.IsAny<IServerEncryption>(), It.IsAny<CancellationToken>()))
                .Callback((string bucketName, string objectName, string filePath, string contentType,
                    Dictionary<string, string> metaData, IServerEncryption sse, CancellationToken cancellationToken) =>
                {
                    File.WriteAllText(filePath, contentStr);
                    Thread.Sleep(2000);
                }).Returns(Task.CompletedTask);

            var objectSystem = new CachedS3ObjectSystem(settings, () => remoteStorage.Object, _objectSystemLogger);

            var memory = new MemoryStream(content);
            objectSystem.PutObjectAsync(bucket, name, memory, memory.Length, "text/plain", null, null, default)
                .Wait();
            
            File.Exists(Path.Combine(fs.TestFolder, bucket, "files", name)).Should().BeTrue();

            await objectSystem.Sync();
            File.Exists(Path.Combine(fs.TestFolder, bucket, "files", name)).Should().BeTrue();

            await objectSystem.RemoveObjectAsync(bucket, name);

            File.Exists(Path.Combine(fs.TestFolder, bucket, "tbd", name)).Should().BeTrue();
        }*/

        [Test]
        public async Task PutObject_File_Ok()
        {
            using var fs = new TestArea(nameof(PutObject_File_Ok));

            var settings = new CachedS3ObjectSystemSettings
            {
                Endpoint = "localhost:9000",
                AccessKey = "minioadmin",
                SecretKey = "minioadmin",
                UseSsl = false,
                AppName = "Registry",
                AppVersion = "1.0",
                Region = "us-east-1",
                CachePath = fs.TestFolder,
                BridgeUrl = "http://localhost:5000/_bridge",
                MaxSize = 50//1024 * 1024
            };

            const string bucket = "admin";
            const string name = "test.txt";

            var remoteStorage = new Mock<IObjectSystem>();

            var contentStr = "Test Test Test";
            var content = Encoding.UTF8.GetBytes(contentStr);
            var memory = new MemoryStream(content);
            var dict = new Dictionary<string, string>();

            remoteStorage.Setup(system => system.PutObjectAsync(bucket, name, It.IsAny<Stream>(), It.IsAny<long>(),
                    It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(),
                    It.IsAny<IServerEncryption>(), It.IsAny<CancellationToken>()))
                .Callback((string bucketName, string objectName, Stream data, long size, string contentType,
                    Dictionary<string, string> metaData, IServerEncryption sse, CancellationToken cancellationToken) =>
                {
                    using var reader = new StreamReader(data);
                    dict.Add(objectName, reader.ReadToEnd());
                    Thread.Sleep(2000);
                }).Returns(Task.CompletedTask);

            var objectSystem = new CachedS3ObjectSystem(settings, () => remoteStorage.Object, _objectSystemLogger);

            await objectSystem.PutObjectAsync(bucket, name, memory, memory.Length, "text/plain", null, null, default);

            (await File.ReadAllTextAsync(Path.Combine(fs.TestFolder, bucket, "descriptors", name + ".json"))).Should()
                .Be(
                    "{\"SyncTime\":null,\"LastError\":null,\"Info\":{\"ContentType\":\"text/plain\",\"MetaData\":null,\"SSE\":null}}");

            (await File.ReadAllTextAsync(Path.Combine(fs.TestFolder, bucket, "files", name))).Should().Be(contentStr);

            await objectSystem.Sync();

            await objectSystem.Cleanup();

        }
    }
}