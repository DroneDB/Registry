using System;
using System.IO;
using System.Text;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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
        private Logger<CachedObjectSystem> _objectSystemLogger;

        public void Setup()
        {
            _objectSystemLogger = new Logger<CachedObjectSystem>(LoggerFactory.Create(builder => builder.AddConsole()));
        }

        [Test]
        public async Task GetObject_ConcurrentMisses()
        {
            using var fs = new TestArea(nameof(GetObject_ConcurrentMisses));

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
                .Callback((string bucketName, string objectName, Action<Stream> action, IServerEncryption sse, CancellationToken token) => 
                    action(new MemoryStream(content)))
                .Returns(Task.CompletedTask);

            var objectSystem = new CachedObjectSystem(settings, () => remoteStorage.Object, _objectSystemLogger);

            var memory = new MemoryStream();

            await objectSystem.GetObjectAsync(bucket, name, stream => stream.CopyTo(memory));

            memory.ToArray().Should().BeEquivalentTo(content);
        }
    }
}