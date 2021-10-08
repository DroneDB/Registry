using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Registry.Common;

namespace Registry.Web.Models.Configuration
{
    public class AppSettings
    {
        /// <summary>
        /// Secret to generate JWT tokens
        /// </summary>
        public string Secret { get; set; }

        /// <summary>
        /// JWT token expiration in days
        /// </summary>
        public int TokenExpirationInDays { get; set; }

        /// <summary>
        /// List of JWT revoked tokens
        /// </summary>
        public string[] RevokedTokens { get; set; }

        /// <summary>
        /// Provider for authentication database
        /// </summary>
        [JsonConverter(typeof(StringEnumConverter))] 
        public DbProvider AuthProvider { get; set; }

        /// <summary>
        /// Provider for registry database
        /// </summary>
        [JsonConverter(typeof(StringEnumConverter))] 
        public DbProvider RegistryProvider { get; set; }

        /// <summary>
        /// Provider for hangfire
        /// </summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public HangfireProvider HangfireProvider { get; set; }
        
        /// <summary>
        /// Default admin details
        /// </summary>
        public AdminInfo DefaultAdmin { get; set; }

        /// <summary>
        /// Storage path for Ddb databases
        /// </summary>
        public string DdbStoragePath { get; set; }

        /// <summary>
        /// Supported Ddb version
        /// </summary>
        public PackageVersion SupportedDdbVersion { get; set; }

        /// <summary>
        /// Max request body size
        /// </summary>
        public long? MaxRequestBodySize { get; set; }

        /// <summary>
        /// Lenght of batch tokens
        /// </summary>
        public int BatchTokenLength { get; set; }

        /// <summary>
        /// Timeout of batch uploads
        /// </summary>
        public TimeSpan UploadBatchTimeout { get; set; }

        /// <summary>
        /// Length of the random generated dataset name
        /// </summary>
        public int RandomDatasetNameLength { get; set; }

        /// <summary>
        /// Name of the auth cookie that contains the jwt token
        /// </summary>
        public string AuthCookieName { get; set; }

        /// <summary>
        /// Overrides the external url
        /// </summary>
        public string ExternalUrlOverride { get; set; }

        /// <summary>
        /// External authentication provider url
        /// </summary>
        public string ExternalAuthUrl { get; set; }

        /// <summary>
        /// Storage provider settings
        /// </summary>
        public StorageProvider StorageProvider { get; set; }

        /// <summary>
        /// Cache provider settings
        /// </summary>
        public CacheProvider CacheProvider { get; set; }

        /// <summary>
        /// Enables the user storage limiter
        /// </summary>
        public bool EnableStorageLimiter { get; set; }

        /// <summary>
        /// Number of worker threads (-1 to use ASP.NET default)
        /// </summary>
        public int WorkerThreads { get; set; }

    }
}
