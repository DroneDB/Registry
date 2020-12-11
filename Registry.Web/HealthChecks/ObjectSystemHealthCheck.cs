using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Registry.Ports.ObjectSystem;

namespace Registry.Web.HealthChecks
{
    public class ObjectSystemHealthCheck : IHealthCheck
    {

        private readonly IObjectSystem _objectSystem;

        // TODO: Move to config and let it be skippable
        private const float FreeSpacePercWarningThreshold = 0.1f;

        public ObjectSystemHealthCheck(IObjectSystem objectSystem)
        {
            _objectSystem = objectSystem;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = new CancellationToken())
        {
         
            var testBucketName = "test-" + Guid.NewGuid();

            var data = new Dictionary<string, object>
            {
                {"TestBucketName", testBucketName},
                {"Provider", _objectSystem.GetType().FullName}
            };

            var res = await _objectSystem.BucketExistsAsync(testBucketName, cancellationToken);
            if (res)
                return HealthCheckResult.Unhealthy("Bucket system not working: found a non-existing bucket", null, data);

            // Quote from S3 docs: https://docs.aws.amazon.com/AmazonS3/latest/gsg/CreatingABucket.html
            // "You are not charged for creating a bucket. You are charged only for storing objects in the bucket and for transferring objects in and out of the bucket."
            // So we will not break the bank doing these checks :)
            await _objectSystem.MakeBucketAsync(testBucketName, null, cancellationToken);

            res = await _objectSystem.BucketExistsAsync(testBucketName, cancellationToken);
            if (!res)
                return HealthCheckResult.Unhealthy("Cannot find the newly created bucket", null, data);

            await _objectSystem.RemoveBucketAsync(testBucketName, cancellationToken);

            res = await _objectSystem.BucketExistsAsync(testBucketName, cancellationToken);
            if (res)
                return HealthCheckResult.Unhealthy("Cannot delete newly created bucket: it was found!", null, data);

            var info = _objectSystem.GetStorageInfo();
            data.Add("StorageTotalSize", info?.TotalSize);
            data.Add("StorageFreeSpace", info?.FreeSpace);
            data.Add("StorageFreeSpacePerc", info?.FreeSpacePerc);

            if (info != null && info.FreeSpacePerc < FreeSpacePercWarningThreshold)
                return HealthCheckResult.Degraded("Low on available disk space", null, data);
            
            return HealthCheckResult.Healthy("The object system is working properly", data);
        }
    }
}
