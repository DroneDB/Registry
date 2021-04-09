using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test
{
    [TestFixture]

    public class ChunkedUploadManagerTest
    {

        private Logger<ChunkedUploadManager> _chunkedUploadManagerLogger;
        private Mock<IOptions<AppSettings>> _appSettingsMock;

        [SetUp]
        public void Setup()
        {
            _appSettingsMock = new Mock<IOptions<AppSettings>>();
            _chunkedUploadManagerLogger = new Logger<ChunkedUploadManager>(LoggerFactory.Create(builder => builder.AddConsole()));

        }

        [Test]
        public void StartSession_BadParameters_Exception()
        {
            using var testArea = new TestArea();
            _settings.UploadPath = testArea.TestFolder;
            Directory.CreateDirectory(_settings.UploadPath);
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);


            var manager = new ChunkedUploadManager(GetTest1Context(), _appSettingsMock.Object, _chunkedUploadManagerLogger);

            manager.Invoking(x => x.InitSession(null, 3, 1000)).Should().Throw<ArgumentException>();
            manager.Invoking(x => x.InitSession(string.Empty, 3, 1000)).Should().Throw<ArgumentException>();
            manager.Invoking(x => x.InitSession("test.txt", 0, 1000)).Should().Throw<ArgumentException>();
            manager.Invoking(x => x.InitSession("test.txt", 3, 0)).Should().Throw<ArgumentException>();

        }

        [Test]
        public async Task EndToEnd_Simple_Ok()
        {
            using var testArea = new TestArea();
            _settings.UploadPath = testArea.TestFolder;
            Directory.CreateDirectory(_settings.UploadPath);
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);

            var manager = new ChunkedUploadManager(GetTest1Context(), _appSettingsMock.Object, _chunkedUploadManagerLogger);

            const string expectedChunk1Str = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. ";
            const string expectedChunk2Str = "Sed vehicula dignissim nunc, quis elementum neque bibendum ac. ";
            const string expectedChunk3Str = "Ut tempus porta eleifend.";

            var sessionId = manager.InitSession("test.txt", 3, 145);
            await manager.Upload(sessionId, GetStringStream(expectedChunk1Str), 0);
            await manager.Upload(sessionId, GetStringStream(expectedChunk2Str), 1);
            await manager.Upload(sessionId, GetStringStream(expectedChunk3Str), 2);

            var targetFile = manager.CloseSession(sessionId);

            var content = await File.ReadAllTextAsync(targetFile);
            content.Should().Be(expectedChunk1Str + expectedChunk2Str + expectedChunk3Str);

        }

        [Test]
        public async Task EndToEnd_Unordered_Ok()
        {
            using var testArea = new TestArea();
            _settings.UploadPath = testArea.TestFolder;
            Directory.CreateDirectory(_settings.UploadPath);
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);

            var manager = new ChunkedUploadManager(GetTest1Context(), _appSettingsMock.Object, _chunkedUploadManagerLogger);

            const string expectedChunk1Str = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. ";
            const string expectedChunk2Str = "Sed vehicula dignissim nunc, quis elementum neque bibendum ac. ";
            const string expectedChunk3Str = "Ut tempus porta eleifend.";

            var sessionId = manager.InitSession("test.txt", 3, 145);
            await manager.Upload(sessionId, GetStringStream(expectedChunk2Str), 1);
            await manager.Upload(sessionId, GetStringStream(expectedChunk3Str), 2);
            await manager.Upload(sessionId, GetStringStream(expectedChunk1Str), 0);

            var targetFile = manager.CloseSession(sessionId);

            var content = await File.ReadAllTextAsync(targetFile);
            content.Should().Be(expectedChunk1Str + expectedChunk2Str + expectedChunk3Str);

        }

        [Test]
        public async Task EndToEnd_MissingChunk_Exception()
        {
            using var testArea = new TestArea();
            _settings.UploadPath = testArea.TestFolder;
            Directory.CreateDirectory(_settings.UploadPath);
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);

            var manager = new ChunkedUploadManager(GetTest1Context(), _appSettingsMock.Object, _chunkedUploadManagerLogger);

            //const string chunk1str = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. ";
            const string expectedChunk2Str = "Sed vehicula dignissim nunc, quis elementum neque bibendum ac. ";
            const string expectedChunk3Str = "Ut tempus porta eleifend.";

            var sessionId = manager.InitSession("test.txt", 3, 145);
            await manager.Upload(sessionId, GetStringStream(expectedChunk2Str), 1);
            await manager.Upload(sessionId, GetStringStream(expectedChunk3Str), 2);
            //manager.Upload(sessionId, GetStringStream(chunk1str), 0);

            manager.Invoking(x => x.CloseSession(sessionId)).Should().Throw<InvalidOperationException>();

        }


        [Test]
        public async Task Upload_BadChunkIndex_Exception()
        {
            using var testArea = new TestArea();
            _settings.UploadPath = testArea.TestFolder;
            Directory.CreateDirectory(_settings.UploadPath);
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);

            var manager = new ChunkedUploadManager(GetTest1Context(), _appSettingsMock.Object, _chunkedUploadManagerLogger);

            const string expectedChunk1Str = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. ";
            const string expectedChunk2Str = "Sed vehicula dignissim nunc, quis elementum neque bibendum ac. ";
            const string expectedChunk3Str = "Ut tempus porta eleifend.";

            var sessionId = manager.InitSession("test.txt", 3, 145);
            await manager.Upload(sessionId, GetStringStream(expectedChunk2Str), 1);
            await manager.Upload(sessionId, GetStringStream(expectedChunk3Str), 2);
            manager.Invoking(async x => await x.Upload(sessionId, GetStringStream(expectedChunk1Str), 3)).Should().Throw<ArgumentException>();

        }

        [Test]
        public async Task EndToEnd_Parallel_Ok()
        {
            using var testArea = new TestArea();
            _settings.UploadPath = testArea.TestFolder;
            Directory.CreateDirectory(_settings.UploadPath);
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);

            var manager = new ChunkedUploadManager(GetTest1Context(), _appSettingsMock.Object, _chunkedUploadManagerLogger);

            const string expectedChunk1Str = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. ";
            const string expectedChunk2Str = "Sed vehicula dignissim nunc, quis elementum neque bibendum ac. ";
            const string expectedChunk3Str = "Ut tempus porta eleifend.";

            var sessionId = manager.InitSession("test.txt", 3, 145);

            var t1 = manager.Upload(sessionId, GetStringStream(expectedChunk1Str), 0);
            var t2 = manager.Upload(sessionId, GetStringStream(expectedChunk2Str), 1);
            var t3 = manager.Upload(sessionId, GetStringStream(expectedChunk3Str), 2);

            Task.WaitAll(t1, t2, t3);

            var targetFile = manager.CloseSession(sessionId);

            var content = await File.ReadAllTextAsync(targetFile);
            content.Should().Be(expectedChunk1Str + expectedChunk2Str + expectedChunk3Str);

        }


        [Test]
        public async Task EndToEnd_Parallel_MultipleSessions_Ok()
        {
            using var testArea = new TestArea();
            _settings.UploadPath = testArea.TestFolder;
            Directory.CreateDirectory(_settings.UploadPath);
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);

            var manager = new ChunkedUploadManager(GetTest1Context(), _appSettingsMock.Object, _chunkedUploadManagerLogger);

            const string expectedChunk1Str = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. ";
            const string expectedChunk2Str = "Sed vehicula dignissim nunc, quis elementum neque bibendum ac. ";
            const string expectedChunk3Str = "Ut tempus porta eleifend.";

            for (var n = 0; n < 10; n++)
            {
                var sessionId = manager.InitSession($"test-{n}.txt", 3, 145);

                var t1 = manager.Upload(sessionId, GetStringStream(expectedChunk1Str), 0);
                var t2 = manager.Upload(sessionId, GetStringStream(expectedChunk2Str), 1);
                var t3 = manager.Upload(sessionId, GetStringStream(expectedChunk3Str), 2);

                Task.WaitAll(t1, t2, t3);

                var targetFile = manager.CloseSession(sessionId);

                var content = await File.ReadAllTextAsync(targetFile);
                content.Should().Be(expectedChunk1Str + expectedChunk2Str + expectedChunk3Str);

            }


        }


        private Stream GetStringStream(string str)
        {
            var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(str));
            memoryStream.Seek(0, SeekOrigin.Begin);
            return memoryStream;
        }

        #region Test Data 

        private readonly AppSettings _settings = JsonConvert.DeserializeObject<AppSettings>(@"{
    ""Secret"": ""a2780070a24cfcaf5a4a43f931200ba0d19d8b86b3a7bd5123d9ad75b125f480fcce1f9b7f41a53abe2ba8456bd142d38c455302e0081e5139bc3fc9bf614497"",
    ""TokenExpirationInDays"": 7,
    ""RevokedTokens"": [
      """"
    ],
    ""AuthProvider"": ""Sqlite"",
    ""RegistryProvider"": ""Sqlite"",
    ""StorageProvider"": {
      ""type"": ""Physical"",
      ""settings"": {
        ""path"": ""./temp""
      }
    },
    ""DefaultAdmin"": {
      ""Email"": ""admin@example.com"",
      ""UserName"": ""admin"",
      ""Password"": ""password""
    },
    ""DdbStoragePath"": ""./ddbstore"",
    ""UploadPath"": ""./uploads"",
    ""MaxUploadChunkSize"": 512000,
    ""ChunkedUploadSessionTimeout"": ""01:00:00"",
    ""MaxRequestBodySize"": 52428800,
    ""SupportedDdbVersion"": {
      ""Major"": 0,
      ""Minor"": 9,
      ""Build"": 4
    },
    ""BatchTokenLength"": 32,
    ""RandomDatasetNameLength"": 16
}
  ");


        #endregion

        #region TestContexts

        private static RegistryContext GetTest1Context()
        {
            var options = new DbContextOptionsBuilder<RegistryContext>()
                .UseInMemoryDatabase(databaseName: "RegistryDatabase-" + Guid.NewGuid())
                .Options;

            // Insert seed data into the database using one instance of the context
            using (var context = new RegistryContext(options))
            {

                var entity = new Organization
                {
                    Slug = MagicStrings.PublicOrganizationSlug,
                    Name = "Public",
                    CreationDate = DateTime.Now,
                    Description = "Public organization",
                    IsPublic = true,
                    OwnerId = null
                };
                var ds = new Dataset
                {
                    Slug = MagicStrings.DefaultDatasetSlug,
                    Name = "Default",
                    Description = "Default dataset",
                    IsPublic = true,
                    CreationDate = DateTime.Now,
                    LastUpdate = DateTime.Now
                };
                entity.Datasets = new List<Dataset> { ds };

                context.Organizations.Add(entity);

                context.SaveChanges();
            }

            return new RegistryContext(options);
        }

        #endregion
    }
}
