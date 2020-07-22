using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Registry.Adapters.ObjectSystem;
using System.IO.Compression;
using System.Reactive.Linq;
using Microsoft.VisualBasic.CompilerServices;
using Registry.Common;
using Registry.Ports.ObjectSystem.Model;

namespace Registry.Adapters.Test.ObjectSystem
{
    [TestFixture]
    public class PhysicalObjectSystemTest
    {

        public const string BaseTestFolder = "PhysicalObjectSystemTest";
        public const string TestArchivesPath = "Data";


        #region General

        [Test]
        public void Ctor_InvalidFolder_Exception()
        {

            const string missingPath = "/wfwefwe/fwefwef/rthtrhtrh";

            FluentActions.Invoking(() =>
            {
                var fs = new PhysicalObjectSystem(missingPath);
            }).Should().Throw<ArgumentException>();


        }

        [Test]
        public void Ctor_ExistingFolder_CreatedOk()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test1.zip"), BaseTestFolder);

            try
            {

                var fs = new PhysicalObjectSystem(test.TestFolder);

            }
            catch (Exception ex)
            {
                Assert.Fail("This path should exist, instead: " + ex.Message);
            }
        }

        #endregion

        #region Buckets

        [Test]
        public async Task BucketExistsAsync_MissingBucket_False()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test1.zip"), BaseTestFolder);

            const string missingBucketName = "wuiohfniwugfnuiweggrweerg";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            var res = await fs.BucketExistsAsync(missingBucketName);

            res.Should().BeFalse();

        }

        [Test]
        public async Task BucketExistsAsync_ExistingBucket_True()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test1.zip"), BaseTestFolder);

            const string existingBucketName = "bucket1";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            var res = await fs.BucketExistsAsync(existingBucketName);

            res.Should().BeTrue();

        }

        [Test]
        public async Task ListBucketsAsync_BucketsList()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test1.zip"), BaseTestFolder);

            string[] expectedBuckets = { "bucket1", "bucket2" };

            var fs = new PhysicalObjectSystem(test.TestFolder);

            var res = await fs.ListBucketsAsync();

            foreach (var bucket in res.Buckets)
            {
                Debug.Write("Found bucket: ");
                Debug.WriteLine(bucket.Name);
            }

            res.Buckets.Select(item => item.Name).OrderBy(item => item).Should().BeEquivalentTo(expectedBuckets);

        }

        [Test]
        public void RemoveBucketAsync_MissingBucket_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test1.zip"), BaseTestFolder);

            const string missingBucketName = "iuwehfoiluwbfgoiuwreg";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.RemoveBucketAsync(missingBucketName);
            }).Should().Throw<ArgumentException>();

        }

        [Test]
        public async Task RemoveBucketAsync_ExistingBucket_BucketRemoved()
        {

            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test2.zip"), BaseTestFolder);

            string[] expectedBuckets = { "bucket2" };

            const string bucketToRemove = "bucket1";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            await fs.RemoveBucketAsync(bucketToRemove);

            var res = await fs.ListBucketsAsync();

            res.Buckets.Select(item => item.Name).OrderBy(item => item).Should().BeEquivalentTo(expectedBuckets);

            // Ensure no metadata / policy are left
            Directory.EnumerateFiles(Path.Combine(test.TestFolder, PhysicalObjectSystem.InfoFolder), bucketToRemove + "*")
                .Should().BeEmpty();

        }

        [Test]
        public void MakeBucketAsync_ExistingBucket_ArgumentException()
        {

            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test1.zip"), BaseTestFolder);

            const string alreadyExistingBucket = "bucket1";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.MakeBucketAsync(alreadyExistingBucket, null);
            }).Should().Throw<ArgumentException>();

        }

        [Test]
        public async Task MakeBucketAsync_NewBucket_BucketCreated()
        {

            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test1.zip"), BaseTestFolder);

            string[] expectedBuckets = { "bucket1", "bucket2", "bucket3" };

            const string newBucketName = "bucket3";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            await fs.MakeBucketAsync(newBucketName, null);

            var res = await fs.ListBucketsAsync();

            res.Buckets.Select(item => item.Name).OrderBy(item => item).Should().BeEquivalentTo(expectedBuckets);

        }

        [Test]
        public void GetPolicyAsync_MissingBucket_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test2.zip"), BaseTestFolder);

            const string missingBucket = "bucket3";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.GetPolicyAsync(missingBucket);
            }).Should().Throw<ArgumentException>();
        }

        [Test]
        public async Task GetPolicyAsync_BucketWithPolicy_BucketPolicy()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test2.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string expectedPolicy = "{\r\n    \"test\": \"test\"\r\n}\r\n";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            var policy = await fs.GetPolicyAsync(bucketName);

            policy.Should().Be(expectedPolicy);

        }

        [Test]
        public async Task GetPolicyAsync_BucketWithoutPolicy_Null()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test2.zip"), BaseTestFolder);

            const string bucketName = "bucket2";
            const string expectedPolicy = null;

            var fs = new PhysicalObjectSystem(test.TestFolder);

            var policy = await fs.GetPolicyAsync(bucketName);

            policy.Should().Be(expectedPolicy);

        }

        [Test]
        public void SetPolicyAsync_MissingBucket_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test2.zip"), BaseTestFolder);

            const string missingBucket = "bucket3";
            const string policy = "{\"test\": \"test\" }";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.SetPolicyAsync(missingBucket, policy);
            }).Should().Throw<ArgumentException>();

        }

        [Test]
        public void SetPolicyAsync_InvalidPolicyJson_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test2.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string policy = "{\"test\", \"test\" }";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.SetPolicyAsync(bucketName, policy);
            }).Should().Throw<ArgumentException>();

        }

        [Test]
        public async Task SetPolicyAsync_ExistingBucket()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test2.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string newPolicy = "{\r\n    \"key\": \"value\"\r\n}\r\n";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            await fs.SetPolicyAsync(bucketName, newPolicy);

            var policy = await fs.GetPolicyAsync(bucketName);

            policy.Should().Be(newPolicy);

        }

        #endregion

        #region Objects


        [Test]
        public void ListObjectsAsync_MissingBucket_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test3.zip"), BaseTestFolder);

            const string missingBucket = "bucket3";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(() =>
            {
                fs.ListObjectsAsync(missingBucket);
            }).Should().Throw<ArgumentException>();

        }


        [Test]
        public void ListObjectsAsync_ListOfObjects()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test3.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            string[] expectedObjectKeys = { "box.png", "cart.png", "flag-ita.jpg", "lock.png", "rep" };
            ulong[] expectedObjectSizes = { 3100, 3190, 16401, 3282, 0 };
            bool[] expectedObjectIsDir = { false, false, false, false, true };

            var fs = new PhysicalObjectSystem(test.TestFolder);

            var objects = fs.ListObjectsAsync(bucketName).ToEnumerable().ToArray();

            objects.Select(item => item.Key).OrderBy(item => item).Should().BeEquivalentTo(expectedObjectKeys);
            objects.Select(item => item.Size).OrderBy(item => item).Should().BeEquivalentTo(expectedObjectSizes);
            objects.Select(item => item.IsDir).OrderBy(item => item).Should().BeEquivalentTo(expectedObjectIsDir);

            // Check objects in subfolder "rep"
            objects = fs.ListObjectsAsync(bucketName, "rep").ToEnumerable().ToArray();

            expectedObjectKeys = new[] { "parse.js", "phone.png" };
            expectedObjectSizes = new ulong[] { 57, 3490 };
            expectedObjectIsDir = new[] { false, false };

            objects.Select(item => item.Key).OrderBy(item => item).Should().BeEquivalentTo(expectedObjectKeys);
            objects.Select(item => item.Size).OrderBy(item => item).Should().BeEquivalentTo(expectedObjectSizes);
            objects.Select(item => item.IsDir).OrderBy(item => item).Should().BeEquivalentTo(expectedObjectIsDir);

        }

        [Test]
        public void GetObjectInfoAsync_MissingBucket_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string missingBucket = "bucket3";
            const string objectName = "flag-ita.jpg";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.GetObjectInfoAsync(missingBucket, objectName);
            }).Should().Throw<ArgumentException>();

        }

        [Test]
        public void GetObjectInfoAsync_MissingObject_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string existingBucket = "bucket1";
            const string missingObjectName = "flag-itaaaaaaaa.jpg";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.GetObjectInfoAsync(existingBucket, missingObjectName);
            }).Should().Throw<ArgumentException>();

        }


        [Test]
        public async Task GetObjectInfoAsync_ExistingObjectWithInfo_ObjectInfo()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string objectName = "flag-ita.jpg";

            const string expectedContentType = "image/jpeg";
            const string expectedEtag = "38a5a1eb86fc84e97ae7a67566420d12-1";
            const long expectedSize = 16401;
            const string expectedMetadataKey = "key";
            const string expectedMetadataValue = "value";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            var info = await fs.GetObjectInfoAsync(bucketName, objectName);

            info.Size.Should().Be(expectedSize);
            info.ContentType.Should().Be(expectedContentType);
            info.ObjectName.Should().Be(objectName);
            info.ETag.Should().Be(expectedEtag);

            info.MetaData.Count.Should().Be(1);
            info.MetaData.ContainsKey(expectedMetadataKey).Should().BeTrue();
            info.MetaData[expectedMetadataKey].Should().Be(expectedMetadataValue);

        }


        [Test]
        public async Task GetObjectInfoAsync_ExistingObjectWithoutInfo_ObjectInfo()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string objectName = "lock.png";

            const string expectedContentType = "image/png";
            const string expectedEtag = "fc9f0320d52ff371200b7b0767424fc8-1";
            const long expectedSize = 3282;

            var fs = new PhysicalObjectSystem(test.TestFolder);

            var info = await fs.GetObjectInfoAsync(bucketName, objectName);

            info.Size.Should().Be(expectedSize);
            info.ContentType.Should().Be(expectedContentType);
            info.ObjectName.Should().Be(objectName);
            info.ETag.Should().Be(expectedEtag);

            info.MetaData.Count.Should().Be(0);
        }

        [Test]
        public async Task GetObjectInfoAsync_ExistingObjectWithoutInfoInSubdirectory_ObjectInfo()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string objectName = "rep/phone.png";

            const string expectedContentType = "image/png";
            const string expectedEtag = "5319fbb9c487037d5c605db4a384869b-1";
            const long expectedSize = 3490;

            var fs = new PhysicalObjectSystem(test.TestFolder);

            var info = await fs.GetObjectInfoAsync(bucketName, objectName);

            info.Size.Should().Be(expectedSize);
            info.ContentType.Should().Be(expectedContentType);
            info.ObjectName.Should().Be(objectName);
            info.ETag.Should().Be(expectedEtag);

            info.MetaData.Count.Should().Be(0);
        }

        [Test]
        public async Task GetObjectInfoAsync_ExistingObjectWithoutBucketInfo_ObjectInfo()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string bucketName = "bucket2";
            const string objectName = "milano-bg.jpg";

            const string expectedContentType = "image/jpeg";
            const string expectedEtag = "18412d191bf153548ce03a1d1c65a073-1";
            const long expectedSize = 225302;

            var fs = new PhysicalObjectSystem(test.TestFolder);

            var info = await fs.GetObjectInfoAsync(bucketName, objectName);

            info.Size.Should().Be(expectedSize);
            info.ContentType.Should().Be(expectedContentType);
            info.ObjectName.Should().Be(objectName);
            info.ETag.Should().Be(expectedEtag);

            info.MetaData.Count.Should().Be(0);
        }

        [Test]
        public void RemoveObjectAsync_MissingBucket_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string missingBucket = "bucket3";
            const string objectName = "flag-ita.jpg";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.RemoveObjectAsync(missingBucket, objectName);
            }).Should().Throw<ArgumentException>();

        }

        [Test]
        public void RemoveObjectAsync_MissingObject_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string missingObject = "flag-itaaaaaa.jpg";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.RemoveObjectAsync(bucketName, missingObject);
            }).Should().Throw<ArgumentException>();

        }

        [Test]
        public async Task RemoveObjectAsync_ExistingObject_ObjectRemoved()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string objectName = "flag-ita.jpg";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            fs.ListObjectsAsync(bucketName).ToEnumerable().Select(item => item.Key).Should().Contain(objectName);

            await fs.RemoveObjectAsync(bucketName, objectName);

            FluentActions.Invoking(async () =>
            {
                var res = await fs.GetObjectInfoAsync(bucketName, objectName);
            }).Should().Throw<ArgumentException>();

            fs.ListObjectsAsync(bucketName).ToEnumerable().Select(item => item.Key).Should().NotContain(objectName);

        }

        [Test]
        public void GetObjectAsync_File_MissingBucket_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string missingBucket = "bucket3";
            const string objectName = "flag-ita.jpg";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.GetObjectAsync(missingBucket, objectName, Path.Combine(Path.GetTempPath(), objectName));
            }).Should().Throw<ArgumentException>();

        }

        [Test]
        public void GetObjectAsync_File_MissingObject_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string missingObject = "flag-itaaaaaa.jpg";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.GetObjectAsync(bucketName, missingObject, Path.Combine(Path.GetTempPath(), missingObject));
            }).Should().Throw<ArgumentException>();

        }

        [Test]
        public async Task GetObjectAsync_File_ExistingObject_FileMatch()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string objectName = "flag-ita.jpg";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            var newFilePath = Path.Combine(Path.GetTempPath(), objectName);

            await fs.GetObjectAsync(bucketName, objectName, newFilePath);

            File.Exists(newFilePath).Should().BeTrue();

            var etag = AdaptersUtils.CalculateMultipartEtag(await File.ReadAllBytesAsync(newFilePath), 1);

            var info = await fs.GetObjectInfoAsync(bucketName, objectName);

            info.ETag.Should().Be(etag);

            File.Delete(newFilePath);

        }

        [Test]
        public void GetObjectAsync_Stream_MissingBucket_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string missingBucket = "bucket3";
            const string objectName = "flag-ita.jpg";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.GetObjectAsync(missingBucket, objectName, s => { });
            }).Should().Throw<ArgumentException>();

        }

        [Test]
        public void GetObjectAsync_Stream_MissingObject_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string missingObject = "flag-itaaaaaa.jpg";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.GetObjectAsync(bucketName, missingObject, s => { });
            }).Should().Throw<ArgumentException>();

        }

        [Test]
        public async Task GetObjectAsync_Stream_ExistingObject_FileMatch()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string objectName = "flag-ita.jpg";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            var newFilePath = Path.Combine(Path.GetTempPath(), objectName);

            if (File.Exists(newFilePath)) File.Delete(newFilePath);

            await fs.GetObjectAsync(bucketName, objectName, s =>
            {
                using var writer = File.OpenWrite(newFilePath);
                s.CopyTo(writer);
            });

            File.Exists(newFilePath).Should().BeTrue();

            var etag = AdaptersUtils.CalculateMultipartEtag(await File.ReadAllBytesAsync(newFilePath), 1);

            var info = await fs.GetObjectInfoAsync(bucketName, objectName);

            info.ETag.Should().Be(etag);

            File.Delete(newFilePath);

        }

        [Test]
        public void GetObjectAsync_StreamChunk_MissingBucket_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string missingBucket = "bucket3";
            const string objectName = "flag-ita.jpg";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.GetObjectAsync(missingBucket, objectName, 0, 4000, s => { });
            }).Should().Throw<ArgumentException>();

        }

        [Test]
        public void GetObjectAsync_StreamChunk_MissingObject_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string missingObject = "flag-itaaaaaa.jpg";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.GetObjectAsync(bucketName, missingObject, 0, 4000, s => { });
            }).Should().Throw<ArgumentException>();

        }

        [Test]
        public async Task GetObjectAsync_StreamChunk_ExistingObject_FileMatch()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string objectName = "flag-ita.jpg";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            var newFilePath = Path.Combine(Path.GetTempPath(), objectName);

            var objectInfo = await fs.GetObjectInfoAsync(bucketName, objectName);

            if (File.Exists(newFilePath)) File.Delete(newFilePath);

            await fs.GetObjectAsync(bucketName, objectName, 0, objectInfo.Size, s =>
            {
                using var writer = File.OpenWrite(newFilePath);
                s.CopyTo(writer);
            });

            File.Exists(newFilePath).Should().BeTrue();

            var etag = AdaptersUtils.CalculateMultipartEtag(await File.ReadAllBytesAsync(newFilePath), 1);

            var info = await fs.GetObjectInfoAsync(bucketName, objectName);

            info.ETag.Should().Be(etag);

            File.Delete(newFilePath);

            // Test with different content
            await fs.GetObjectAsync(bucketName, objectName, 0, 100, s =>
            {
                using var writer = File.OpenWrite(newFilePath);
                s.CopyTo(writer);
            });

            File.Exists(newFilePath).Should().BeTrue();

            etag = AdaptersUtils.CalculateMultipartEtag(await File.ReadAllBytesAsync(newFilePath), 1);

            info.ETag.Should().NotBe(etag);

            File.Delete(newFilePath);

        }

        [Test]
        public void RemoveIncompleteUploadAsync_MissingBucket_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string missingBucket = "bucket3";
            const string objectName = "flag-ita.jpg";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.RemoveIncompleteUploadAsync(missingBucket, objectName);
            }).Should().Throw<ArgumentException>();

        }

        [Test]
        public void RemoveIncompleteUploadAsync_MissingObject_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string missingObject = "flag-itaaaaaa.jpg";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.RemoveIncompleteUploadAsync(bucketName, missingObject);
            }).Should().Throw<ArgumentException>();

        }

        [Test]
        public void RemoveIncompleteUploadAsync_ExistingObject_NotSupportedException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string missingObject = "flag-ita.jpg";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.RemoveIncompleteUploadAsync(bucketName, missingObject);
            }).Should().Throw<NotSupportedException>();

        }

        [Test]
        public void ListIncompleteUploads_MissingBucket_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string missingBucket = "bucket3";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.ListIncompleteUploads(missingBucket);
            }).Should().Throw<ArgumentException>();

        }

        [Test]
        public void ListIncompleteUploads_ExistingBucket_EmptyObservable()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string missingBucket = "bucket1";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            fs.ListIncompleteUploads(missingBucket).ToEnumerable().Should().BeEmpty();

        }

        [Test]
        public void CopyObjectAsync_MissingSourceBucket_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string missingBucket = "bucket3";
            const string sourceObjectName = "flag-ita.jpg";

            const string destBucket = "bucket2";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.CopyObjectAsync(missingBucket, sourceObjectName, destBucket);
            }).Should().Throw<ArgumentException>();

        }

        [Test]
        public void CopyObjectAsync_MissingObject_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string missingObject = "flag-itaaaaaa.jpg";

            const string destBucket = "bucket2";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.CopyObjectAsync(bucketName, missingObject, destBucket);
            }).Should().Throw<ArgumentException>();

        }

        [Test]
        public void CopyObjectAsync_MissingDestBucket_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string objectName = "flag-itaaaaaa.jpg";

            const string missingBucket = "bucket3";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.CopyObjectAsync(bucketName, objectName, missingBucket);
            }).Should().Throw<ArgumentException>();

        }

        [Test]
        public void CopyObjectAsync_CopyConditions_NotImplemented()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string objectName = "flag-ita.jpg";

            const string destBucketName = "bucket2";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.CopyObjectAsync(bucketName, objectName, destBucketName, null, new Dictionary<string, string>());
            }).Should().Throw<NotImplementedException>();

        }

        [Test]
        public async Task CopyObjectAsync_ExistingSourceObject_FileCopied()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string objectName = "flag-ita.jpg";

            const string destBucketName = "bucket2";

            var fs = new PhysicalObjectSystem(test.TestFolder);

            var sourceFileInfo = await fs.GetObjectInfoAsync(bucketName, objectName);

            await fs.CopyObjectAsync(bucketName, objectName, destBucketName);

            var destFileInfo = await fs.GetObjectInfoAsync(destBucketName, objectName);

            destFileInfo.Should().BeEquivalentTo(sourceFileInfo);

        }

        [Test]
        public void PutObjectAsync_MissingObject_ArgumentException()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string missingBucket = "bucket3";
            const string objectName = "new.txt";

            const string fileContent = "Hello World";

            using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(fileContent));

            var fs = new PhysicalObjectSystem(test.TestFolder);

            FluentActions.Invoking(async () =>
            {
                await fs.PutObjectAsync(missingBucket, objectName, memoryStream, fileContent.Length);
            }).Should().Throw<ArgumentException>();
        }

        [Test]
        public async Task PutObjectAsync_ExistingObject_ObjectCreated()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string objectName = "new.txt";

            const string fileContent = "Hello World";

            const int expectedObjectSize = 11;
            const string expectedObjectContentType = "text/plain";
            const string expectedETag = "b10a8db164e0754105b7a99be72e3fe5-1";
            
            await using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(fileContent));

            var fs = new PhysicalObjectSystem(test.TestFolder);
            
            await fs.PutObjectAsync(bucketName, objectName, memoryStream, fileContent.Length);

            var info = await fs.GetObjectInfoAsync(bucketName, objectName);

            info.ObjectName.Should().Be(objectName);
            info.ContentType.Should().Be(expectedObjectContentType);
            info.ETag.Should().Be(expectedETag);
            info.Size.Should().Be(expectedObjectSize);
            info.MetaData.Should().BeEmpty();

            var newFilePath = Path.Combine(Path.GetTempPath(), objectName);

            if (File.Exists(newFilePath)) File.Delete(newFilePath);

            await fs.GetObjectAsync(bucketName, objectName, newFilePath);

            File.Exists(newFilePath).Should().BeTrue();

            var etag = AdaptersUtils.CalculateMultipartEtag(await File.ReadAllBytesAsync(newFilePath), 1);

            etag.Should().Be(info.ETag);

        }

        [Test]
        public async Task PutObjectAsync_ExistingObjectWithMetadataAndContentType_ObjectCreated()
        {
            using var test = new TestFS(Path.Combine(TestArchivesPath, "Test4.zip"), BaseTestFolder);

            const string bucketName = "bucket1";
            const string objectName = "new.txt";

            const string fileContent = "Hello World";

            const int expectedObjectSize = 11;
            const string expectedObjectContentType = "text/css";
            const string expectedETag = "b10a8db164e0754105b7a99be72e3fe5-1";

            var metadata = new Dictionary<string, string> {{"test", "hello"}};

            await using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(fileContent));

            var fs = new PhysicalObjectSystem(test.TestFolder);

            await fs.PutObjectAsync(bucketName, objectName, memoryStream, fileContent.Length, "text/css", metadata);

            var info = await fs.GetObjectInfoAsync(bucketName, objectName);

            info.ObjectName.Should().Be(objectName);
            info.ContentType.Should().Be(expectedObjectContentType);
            info.ETag.Should().Be(expectedETag);
            info.Size.Should().Be(expectedObjectSize);
            info.MetaData.Should().BeEquivalentTo(metadata);

        }


        #endregion


        [OneTimeTearDown]
        public void Cleanup()
        {
            Debug.WriteLine("Removing orphaned test folders");
            Directory.Delete(Path.Combine(Path.GetTempPath(), BaseTestFolder), true);
        }



    }
}
