using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Registry.Adapters.ObjectSystem;

namespace Registry.Adapters.Test.ObjectSystem
{
    public class PhysicalObjectSystemTest
    {

        private readonly string _physicalPathTest1 = Path.Combine("Data", "Test1");

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
            
            try
            {

                new PhysicalObjectSystem(_physicalPathTest1);

            }
            catch (Exception ex)
            {
                Assert.Fail("This path should exist");
            }

        }

        [Test]
        public async Task BucketExistsAsync_MissingBucket_False()
        {
            const string missingBucketName = "wuiohfniwugfnuiweggrweerg";

            var fs = new PhysicalObjectSystem(_physicalPathTest1);

            var res = await fs.BucketExistsAsync(missingBucketName);

            res.Should().BeFalse();

        }

        [Test]
        public async Task BucketExistsAsync_ExistingBucket_True()
        {
            const string existingBucketName = "bucket1";

            var fs = new PhysicalObjectSystem(_physicalPathTest1);

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


    }
}
