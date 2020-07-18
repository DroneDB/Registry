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
using Microsoft.VisualBasic.CompilerServices;
using Registry.Common;

namespace Registry.Adapters.Test.ObjectSystem
{
    [TestFixture]
    public class PhysicalObjectSystemTest
    {

        public const string BaseTestFolder = "PhysicalObjectSystemTest";
        public const string TestArchivesPath = "Data";

        [Test]
        public void Ctor_InvalidFolder_Exception()
        {

            const string missingPath = "/wfwefwe/fwefwef/rthtrhtrh";

            try
            {

                var fs = new PhysicalObjectSystem(missingPath);

                Assert.Fail("No exception was thrown with invalid folder");
            }
            catch (ArgumentException ex)
            {
                Assert.Pass();
            }
            catch (Exception ex)
            {
                Assert.Fail("Wrong exception type thrown: expected ArgumentException");
            }

        }

        [Test]
        public void Ctor_ExistingFolder_CreatedOk()
        {
            using var fs = new TestFS(Path.Combine(TestArchivesPath, "Test1.zip"), BaseTestFolder);

            try
            {

                new PhysicalObjectSystem(fs.TestFolder);

            }
            catch (Exception ex)
            {
                Assert.Fail("This path should exist");
            }
        }

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

        /*
        [Test]
        public async Task ListBucketsAsync_BucketList()
        {
            string expecetedBucketNames = "bucket1";

            var fs = new PhysicalObjectSystem(_physicalPathTest1);

            var res = await fs.ListBucketsAsync();

            // No owner
            res.Owner.Should().BeNullOrEmpty();
            

            res.Should().BeTrue();

        }*/

        [OneTimeTearDown]
        public void Cleanup()
        {
            Debug.WriteLine("Removing orphaned test folders");
            Directory.Delete(Path.Combine(Path.GetTempPath(), BaseTestFolder), true);
        }

        

    }
}
