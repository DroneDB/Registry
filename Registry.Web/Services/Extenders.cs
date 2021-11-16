using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities;

namespace Registry.Web.Services
{
    public static class Extenders
    {

        // public static async Task EnsureBucketExists<T>(this IObjectSystem objectSystem, string bucketName, string location, ILogger<T> logger)
        // {
        //     if (!await objectSystem.BucketExistsAsync(bucketName))
        //     {
        //
        //         logger.LogInformation($"Bucket '{bucketName}' does not exist, creating it");
        //
        //         await objectSystem.MakeBucketAsync(bucketName, location);
        //
        //         logger.LogInformation("Bucket created");
        //     }
        //     else
        //     {
        //         logger.LogInformation($"Bucket '{bucketName}' already exists");
        //     }
        //
        // }

        public static string SafeGetLocation<T>(this AppSettings settings, ILogger<T> logger)
        {
            if (settings.StorageProvider.Type != StorageType.S3 && settings.StorageProvider.Type != StorageType.CachedS3) return null;

            var st = settings.StorageProvider.Settings.ToObject<S3StorageProviderSettings>();
            if (st == null)
            {
                logger.LogWarning("No S3 settings loaded, shouldn't this be a problem?");
                return null;
            }

            if (st.Region == null)
                logger.LogWarning("No region specified in storage provider config");

            return st.Region;
        }

    }
}
